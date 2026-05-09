//! Unified command dispatch — the Rust equivalent of C# ProcessCommand().
//!
//! Every command goes through this path: REPL, scripts, function bodies,
//! init.rush, -c mode. This ensures consistent behavior everywhere.

use crate::ai;
use crate::eval::Evaluator;
use crate::flags;
use crate::jobs::JobTable;
use crate::parser;
use crate::pipeline;
use crate::process;
use crate::triage;
use crate::value::Value;

/// Result of dispatching a command.
pub struct DispatchResult {
    pub exit_code: i32,
    pub should_exit: bool,
}

/// Shell builtins that must run in-process. Returns Some(exit_code) if handled.
pub type BuiltinHandler = dyn FnMut(&str, &str) -> Option<i32>;

/// Callbacks the higher layer (rush-cli) supplies so pipelines can route
/// their RHS into in-process builtins instead of execve'ing them (which
/// fails with 127 for names that aren't on PATH). The two callbacks are:
///
/// - `is_builtin(name)` — cheap predicate: is `name` something the host
///   layer owns? Used by dispatch to distinguish "unknown command" (fall
///   through to the native pipe chain) from "builtin that didn't want
///   this input" (emit a clear error).
/// - `handle_pipe(name, args, stdin)` — attempt to run `name args` as a
///   pipeline RHS with the upstream output as stdin. Return `Some(code)`
///   if the builtin consumed/handled the stdin, or `None` if it didn't
///   (e.g. `cd`, `exit` — builtins that have no stdin semantics).
pub struct PipelineBuiltins<'a> {
    pub is_builtin: &'a dyn Fn(&str) -> bool,
    #[allow(clippy::type_complexity)]
    pub handle_pipe: &'a mut dyn FnMut(&str, &str, &[u8]) -> Option<i32>,
}

/// Dispatch a command line through the full pipeline.
/// This is the single entry point for all command execution.
/// Builtins (cd, export, alias) should be checked by the caller before dispatch.
pub fn dispatch(
    line: &str,
    evaluator: &mut Evaluator,
    _builtin_handler: Option<&mut BuiltinHandler>,
) -> DispatchResult {
    dispatch_with_jobs(line, evaluator, None)
}

/// Dispatch with optional job table for background job support.
pub fn dispatch_with_jobs(
    line: &str,
    evaluator: &mut Evaluator,
    job_table: Option<&mut JobTable>,
) -> DispatchResult {
    dispatch_with_jobs_and_builtins(line, evaluator, job_table, None)
}

/// Dispatch with optional job table AND rush-cli builtin callbacks for
/// pipeline-RHS routing. Called from the rush-cli main loop; other
/// callers (llm/mcp, tests, eval-invoked-dispatch) use `dispatch_with_jobs`
/// and get the no-pipeline-builtin behavior.
pub fn dispatch_with_jobs_and_builtins(
    line: &str,
    evaluator: &mut Evaluator,
    mut job_table: Option<&mut JobTable>,
    mut pipeline_builtins: Option<&mut PipelineBuiltins>,
) -> DispatchResult {
    let trimmed = line.trim();
    if trimmed.is_empty() {
        return DispatchResult { exit_code: 0, should_exit: false };
    }

    // Step 0: Handle heredocs — extract <<DELIM content from multi-line input
    let trimmed = expand_heredocs(trimmed);
    let trimmed = trimmed.as_str();

    // Step 1: Split on chain operators (&&, ||, ;) respecting quotes
    let chains = split_chains(trimmed);

    let mut last_exit: i32 = 0;
    let mut last_failed = false;
    let mut should_exit = false;

    for chain in &chains {
        let segment = chain.command.trim();
        if segment.is_empty() {
            continue;
        }

        // Chain logic: skip based on && / || / ;
        match chain.operator {
            ChainOp::And if last_failed => continue,
            ChainOp::Or if !last_failed => continue,
            _ => {}
        }

        // Step 2: Extract inline env vars (VAR=val cmd) and pure
        // assignments (VAR=val with no command).
        let (inline_vars, segment) = extract_inline_env_vars(segment);
        // Pure assignment: set the vars *permanently* (for the rest of
        // the dispatch session) and move on to the next chain segment.
        // POSIX `foo=hello` without a following command sets the
        // variable for the shell session. This branch is also what
        // makes `foo="hello"; echo $foo` work — without it, the
        // assignment got set/restored in the same segment and was
        // gone before the next segment ran.
        if !inline_vars.is_empty() && segment.trim().is_empty() {
            for (key, val) in &inline_vars {
                unsafe { std::env::set_var(key, val) };
            }
            last_exit = 0;
            last_failed = false;
            evaluator.exit_code = 0;
            continue;
        }
        let segment = segment.as_str();

        // Set inline vars (scoped to this segment only)
        let mut saved_vars: Vec<(String, Option<String>)> = Vec::new();
        for (key, val) in &inline_vars {
            saved_vars.push((key.clone(), std::env::var(key).ok()));
            unsafe { std::env::set_var(key, val) };
        }

        // Step 2b: Check for background &
        let (is_background, segment) = if segment.ends_with(" &") || segment.ends_with("\t&") {
            (true, segment[..segment.len() - 1].trim())
        } else if segment == "&" {
            continue; // bare & is meaningless
        } else {
            (false, segment)
        };

        // Background job — spawn and continue
        if is_background {
            if let Some(jt) = job_table.as_deref_mut() {
                match process::spawn_background(segment) {
                    Ok((pid, pgid)) => {
                        jt.add(pid, pgid, segment);
                        unsafe { std::env::set_var("RUSH_LAST_BG_PID", pid.to_string()) };
                        last_exit = 0;
                        last_failed = false;
                    }
                    Err(e) => {
                        eprintln!("{e}");
                        last_exit = 127;
                        last_failed = true;
                    }
                }
            } else {
                // No job table — just spawn and forget
                match process::spawn_background(segment) {
                    Ok((pid, _)) => {
                        eprintln!("[bg] {pid}");
                        last_exit = 0;
                    }
                    Err(e) => {
                        eprintln!("{e}");
                        last_exit = 127;
                        last_failed = true;
                    }
                }
            }
            evaluator.exit_code = last_exit;
            // Restore inline env vars before continuing
            for (key, prev) in saved_vars {
                match prev {
                    Some(val) => unsafe { std::env::set_var(&key, &val) },
                    None => unsafe { std::env::remove_var(&key) },
                }
            }
            continue;
        }

        // Step 2c: Check for ! negation
        let (negate, segment) = if let Some(rest) = segment.strip_prefix("! ") {
            (true, rest)
        } else {
            (false, segment)
        };

        // Step 2d: Expand `#{...}` interpolation on shell-command segments
        // (#272). Rush-syntax segments are skipped — the parser owns their
        // string-literal interpolation. Single-quoted regions are
        // preserved literal; double-quoted and unquoted regions expand.
        // Touches every downstream path (cd, export, unset, run_native,
        // pipe chains, subshell), so `cd "#{name}"`, `git -C "#{dir}"`,
        // and friends all see the substituted value.
        let segment_expanded;
        let segment: &str = if triage::is_rush_syntax(segment) {
            segment
        } else {
            segment_expanded = expand_shell_interpolation(segment, evaluator);
            &segment_expanded
        };

        // set -x: print command before execution
        if flags::xtrace() {
            eprintln!("+ {segment}");
        }

        // Bare-dot shortcut: `..` → `cd ..`, `...` → `cd ../..`,
        // `....` → `cd ../../..`, etc. Carried over from the .NET
        // version of rush. Only triggers on a segment that is *only*
        // dots (no whitespace, no other chars), so a literal `.foo`
        // command or `cd ..` itself is unaffected.
        let dot_expanded;
        let segment: &str = if let Some(replacement) = expand_bare_dots(segment) {
            dot_expanded = replacement;
            &dot_expanded
        } else {
            segment
        };

        let first_word = segment.split_whitespace().next().unwrap_or("");

        // Core builtins handled directly in dispatch (work in chains)
        if first_word == "set" {
            let args = segment[first_word.len()..].trim();
            if flags::handle_set_flag(args) {
                last_exit = 0;
                last_failed = false;
                evaluator.exit_code = 0;
                // Restore inline env vars
                for (key, prev) in saved_vars {
                    match prev {
                        Some(val) => unsafe { std::env::set_var(&key, &val) },
                        None => unsafe { std::env::remove_var(&key) },
                    }
                }
                continue;
            }
        }

        // Subshell: ( list ) — execute in child process
        // Uses Rush's own binary so subshell gets Rush syntax
        if segment.starts_with('(') && segment.ends_with(')') {
            let inner = &segment[1..segment.len() - 1];
            // Try to find our own binary
            let rush_bin = std::env::current_exe()
                .map(|p| p.to_string_lossy().to_string())
                .unwrap_or_else(|_| "rush".to_string());
            let result = process::run_native_capture(&format!("{rush_bin} -c '{}'", inner.replace('\'', "'\\''")));
            if !result.stdout.is_empty() {
                print!("{}", result.stdout);
            }
            last_exit = result.exit_code;
            last_failed = last_exit != 0;
            evaluator.exit_code = last_exit;
            // Restore inline env vars
            for (key, prev) in saved_vars {
                match prev {
                    Some(val) => unsafe { std::env::set_var(&key, &val) },
                    None => unsafe { std::env::remove_var(&key) },
                }
            }
            continue;
        }

        // Brace group: { list; } — execute in current shell
        if segment.starts_with("{ ") && segment.ends_with(" }") {
            let inner = &segment[2..segment.len() - 2];
            // Recursively dispatch the inner commands
            let inner_result = dispatch_with_jobs(inner, evaluator, job_table.as_deref_mut());
            last_exit = inner_result.exit_code;
            last_failed = last_exit != 0;
            if inner_result.should_exit { should_exit = true; break; }
            // Restore inline env vars
            for (key, prev) in saved_vars {
                match prev {
                    Some(val) => unsafe { std::env::set_var(&key, &val) },
                    None => unsafe { std::env::remove_var(&key) },
                }
            }
            continue;
        }

        // Core builtins handled directly in dispatch (work in chains)
        if first_word == ":" || first_word == "true" {
            last_exit = 0;
            last_failed = false;
            evaluator.exit_code = 0;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }
        if first_word == "false" {
            last_exit = 1;
            last_failed = true;
            evaluator.exit_code = 1;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }

        // export — must work in chains: export FOO=bar; echo $FOO
        if first_word == "export" {
            let args = segment[first_word.len()..].trim();
            if let Some((key, value)) = args.split_once('=') {
                let key = key.trim();
                let value = value.trim().trim_matches('"').trim_matches('\'');
                // Expand Rush-side #{expr} interpolation before the value
                // reaches the environment — `export T="#{target}"` used to
                // store the literal text `#{target}` (#255).
                let expanded = evaluator.expand_interpolation(value);
                unsafe { std::env::set_var(key, expanded) };
            }
            last_exit = 0;
            last_failed = false;
            evaluator.exit_code = 0;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }

        // unset — must work in chains
        if first_word == "unset" {
            let args = segment[first_word.len()..].trim();
            for name in args.split_whitespace() {
                unsafe { std::env::remove_var(name) };
            }
            last_exit = 0;
            last_failed = false;
            evaluator.exit_code = 0;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }

        // cd — must work in chains: cd /tmp && pwd
        if first_word == "cd" {
            // Parse through the shared quote/escape-aware tokenizer so
            // `cd "Application Support"` and `cd Application\ Support`
            // resolve to a single path argument. Tilde expansion is only
            // applied to an unquoted `~` at the start; quoted `"~"` would
            // ideally stay literal, but that edge case isn't worth extra
            // plumbing today. On Windows, swap backslash path separators
            // to `/` before parsing — `parse_command_line` treats `\` as
            // a POSIX escape, which would strip Windows path separators.
            #[cfg(windows)]
            let parse_input: std::borrow::Cow<str> = if segment.contains('\\') {
                std::borrow::Cow::Owned(segment.replace('\\', "/"))
            } else {
                std::borrow::Cow::Borrowed(segment)
            };
            #[cfg(not(windows))]
            let parse_input: &str = segment;
            // .as_ref() is load-bearing on Windows where parse_input is Cow<str>;
            // clippy only sees the non-Windows branch where it's a no-op on &str.
            #[allow(clippy::useless_asref)]
            let parts = process::parse_command_line(parse_input.as_ref());
            let target = parts.get(1).map(String::as_str).unwrap_or("");
            let path = if target.is_empty() || target == "~" {
                std::env::var("HOME").unwrap_or_else(|_| ".".into())
            } else if let Some(rest) = target.strip_prefix("~/") {
                format!("{}/{rest}", std::env::var("HOME").unwrap_or_default())
            } else {
                target.to_string()
            };
            match std::env::set_current_dir(&path) {
                Ok(()) => { last_exit = 0; last_failed = false; }
                Err(e) => { eprintln!("cd: {path}: {e}"); last_exit = 1; last_failed = true; }
            }
            evaluator.exit_code = last_exit;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }

        // exit/quit
        if first_word == "exit" || first_word == "quit" {
            should_exit = true;
            break;
        }

        // Step 3: Check for pipes
        let pipe_segments = pipeline::split_pipeline(segment);

        // Special case: `... | ai [args]` — capture upstream output
        // and pass it to the ai builtin via piped_input. Without this,
        // the native pipeline path tries to execve `ai` (a rush-cli
        // builtin, not on PATH) and fails with 127, blaming the first
        // word in the line rather than the real missing command.
        if pipe_segments.len() > 1 {
            if let Some(last) = pipe_segments.last() {
                let last_first = last.split_whitespace().next().unwrap_or("");
                if last_first == "ai" {
                    let upstream_cmd = pipe_segments[..pipe_segments.len() - 1].join(" | ");
                    let upstream = process::run_native_capture(&upstream_cmd);
                    if !upstream.stderr.is_empty() {
                        eprintln!("{}", upstream.stderr);
                    }
                    let ai_args = last.trim_start().strip_prefix("ai").unwrap_or("").trim();
                    let (prompt, provider, model) = ai::parse_ai_args(ai_args);
                    let exit = if prompt.is_empty() {
                        eprintln!("ai: missing prompt — usage: ... | ai \"question\"");
                        2
                    } else {
                        match ai::execute(
                            provider.as_deref(),
                            model.as_deref(),
                            &prompt,
                            Some(&upstream.stdout),
                        ) {
                            Ok(_) => 0,
                            Err(e) => {
                                eprintln!("ai: {e}");
                                1
                            }
                        }
                    };
                    last_exit = exit;
                    last_failed = exit != 0;
                    evaluator.exit_code = exit;
                    for (key, prev) in saved_vars {
                        match prev {
                            Some(val) => unsafe { std::env::set_var(&key, &val) },
                            None => unsafe { std::env::remove_var(&key) },
                        }
                    }
                    continue;
                }
            }
        }

        // Pipeline LHS that's a rush-cli builtin: `alias | grep foo`,
        // `history | tail`, `path | where /bin/`. The native pipe chain
        // below can't execve the builtin name, so we capture its stdout
        // via fd redirection and feed those bytes into the remainder of
        // the pipeline (Rush pipe ops or a native tail). #236.
        if pipe_segments.len() > 1 {
            let first = &pipe_segments[0];
            let first_first = first.split_whitespace().next().unwrap_or("");
            let lhs_is_builtin = pipeline_builtins
                .as_deref_mut()
                .map(|b| !first_first.is_empty() && (b.is_builtin)(first_first))
                .unwrap_or(false);
            if lhs_is_builtin {
                let builtins = pipeline_builtins.as_deref_mut().expect("lhs_is_builtin implies Some");
                let args = first
                    .trim_start()
                    .strip_prefix(first_first)
                    .unwrap_or("")
                    .trim_start();
                let bytes = capture_builtin_stdout(builtins, first_first, args, &[]);
                let remaining: Vec<String> = pipe_segments[1..].to_vec();

                let code = if remaining.is_empty() {
                    // Degenerate case — shouldn't happen since len > 1.
                    0
                } else if segments_need_rush_pipeline(&remaining, pipeline_builtins.as_deref()) {
                    // Rush pipe ops or mid-/end-pipe builtins downstream:
                    // convert captured bytes to an array of lines and
                    // thread through the pipe-op chain (which is now
                    // builtin-aware for shell-segment stages).
                    let text = String::from_utf8_lossy(&bytes).into_owned();
                    let initial = pipeline::text_to_array(&text);
                    run_pipeline_from_value(
                        evaluator,
                        initial,
                        &remaining,
                        pipeline_builtins.as_deref_mut(),
                    )
                } else {
                    // Native tail — grep, awk, head, etc. Spawn the
                    // chain with the first stage's stdin filled from
                    // the captured bytes.
                    run_native_chain_with_initial_bytes(&remaining, &bytes)
                };

                last_exit = code;
                last_failed = code != 0;
                evaluator.exit_code = code;
                for (key, prev) in saved_vars {
                    match prev {
                        Some(val) => unsafe { std::env::set_var(&key, &val) },
                        None => unsafe { std::env::remove_var(&key) },
                    }
                }
                continue;
            }
        }

        if pipe_segments.len() > 1 {
            // Value-source entry: if the first segment evaluates as a
            // Rush value (literal, bound variable, method call, $()),
            // thread the evaluated Value straight into the pipeline's
            // value-flow path. Covers shell terminal stages ("x" | cat),
            // pipe-op stages ([1,2,3] | count), and value-first mixed
            // chains (#265 Phase 2). Done here — before the pipe-op /
            // builtin check below — so the Value is evaluated exactly
            // once regardless of downstream routing.
            if let Some(val) =
                try_eval_as_value_source(&pipe_segments[0], evaluator)
            {
                let code = run_pipeline_from_value(
                    evaluator,
                    val,
                    &pipe_segments[1..],
                    pipeline_builtins.as_deref_mut(),
                );
                last_exit = code;
                last_failed = code != 0;
                evaluator.exit_code = code;
                for (key, prev) in saved_vars {
                    match prev {
                        Some(val) => unsafe { std::env::set_var(&key, &val) },
                        None => unsafe { std::env::remove_var(&key) },
                    }
                }
                continue;
            }

            // Route through Rush's value-flow pipeline when any segment
            // is a Rush pipe op or a rush-cli builtin in a middle/terminal
            // position (#224 RHS, #238 middle). The LHS-builtin case is
            // already handled above. Everything else stays on the native
            // fork/exec/dup2 path.
            let need_rush_pipeline = segments_need_rush_pipeline(
                &pipe_segments,
                pipeline_builtins.as_deref(),
            );
            if need_rush_pipeline {
                let code = run_pipeline(
                    evaluator,
                    &pipe_segments,
                    pipeline_builtins.as_deref_mut(),
                );
                last_exit = code;
                last_failed = code != 0;
                evaluator.exit_code = code;
            } else {
                // Pure shell pipeline — native pipe chain (fork/exec/dup2)
                let result = process::run_native(segment);
                last_exit = result.exit_code;
                last_failed = last_exit != 0;
                evaluator.exit_code = last_exit;
            }
            continue;
        }

        // Step 4: Triage — Rush syntax or shell command?
        if triage::is_rush_syntax(segment) {
            match parser::parse(segment) {
                Ok(nodes) => {
                    match evaluator.exec_toplevel(&nodes) {
                        Ok(_) => {
                            last_exit = evaluator.exit_code;
                            last_failed = last_exit != 0;
                        }
                        Err(e) => {
                            eprintln!("rush: {e}");
                            last_exit = 1;
                            last_failed = true;
                            evaluator.exit_code = 1;
                        }
                    }
                }
                Err(_) => {
                    // Parse failed — run natively (fork/exec, TTY preserved)
                    let result = process::run_native(segment);
                    last_exit = result.exit_code;
                    last_failed = last_exit != 0;
                    evaluator.exit_code = last_exit;
                    if !result.stderr.is_empty() {
                        eprintln!("{}", result.stderr);
                    }
                }
            }
        } else if evaluator.env.functions.contains_key(first_word) {
            // #280: bare segment whose first word matches a defined Rush
            // function — invoke the function rather than fork/exec.
            // Mirrors POSIX shell lookup order (function before PATH).
            // Args after first_word are tokenized shell-style and
            // passed as Rush strings; quoted args stay grouped.
            let parts = process::parse_command_line(segment);
            let args: Vec<Value> = parts
                .into_iter()
                .skip(1)
                .map(Value::String)
                .collect();
            let name = first_word.to_string();
            match evaluator.call_function(&name, &args) {
                Ok(_) => {
                    last_exit = evaluator.exit_code;
                    last_failed = last_exit != 0;
                }
                Err(_) => {
                    last_exit = 1;
                    last_failed = true;
                    evaluator.exit_code = 1;
                }
            }
        } else {
            // External command — fork/exec with inherited TTY
            let result = process::run_native(segment);
            last_exit = result.exit_code;
            last_failed = last_exit != 0;
            evaluator.exit_code = last_exit;
            if !result.stderr.is_empty() {
                eprintln!("{}", result.stderr);
            }
        }

        // Apply ! negation
        if negate {
            last_exit = if last_exit == 0 { 1 } else { 0 };
            last_failed = last_exit != 0;
            evaluator.exit_code = last_exit;
        }

        // Restore inline env vars
        for (key, prev) in saved_vars {
            match prev {
                Some(val) => unsafe { std::env::set_var(&key, &val) },
                None => unsafe { std::env::remove_var(&key) },
            }
        }

        // Update $? for special parameter expansion — thread-local
        // cell rather than a process-global env var (#229), so parallel
        // tests don't race on the same variable.
        process::set_last_exit_code(last_exit);

        // set -e: exit on failure (with POSIX exceptions)
        // Exceptions: condition of if/while/until, left side of && or ||
        if flags::errexit() && last_failed {
            // Check if this segment was in an exception context
            let in_exception = matches!(chain.operator, ChainOp::And | ChainOp::Or);
            if !in_exception {
                should_exit = true;
                break;
            }
        }
    }

    DispatchResult {
        exit_code: last_exit,
        should_exit,
    }
}

/// Dispatch for use inside the evaluator (function bodies, etc.)
/// No builtin handler — just triage + eval + shell fallback.
pub fn dispatch_simple(line: &str, evaluator: &mut Evaluator) -> i32 {
    let result = dispatch(line, evaluator, None);
    result.exit_code
}

// ── Shell-segment interpolation (#272) ──────────────────────────────

/// Expand `#{...}` interpolation in a shell-command segment, respecting
/// single-quote vs double-quote boundaries. Single-quoted runs pass
/// through literally; double-quoted and unquoted runs are handed to
/// Bare-dot shortcut: `..` → `cd ..`, `...` → `cd ../..`, etc.
/// Returns `Some(replacement)` if the trimmed segment is *only* dots
/// (>=2), `None` otherwise. Carried over from the .NET version of rush.
///
/// Triggers only when the entire segment after trim is dots — so
/// `cd ..` (which starts with `c`), `.foo` (mixed), and `..rs` (mixed)
/// are unaffected. A single `.` is *not* expanded; that's the POSIX
/// "source" builtin and the no-op cd-here, both of which we don't want
/// to override.
fn expand_bare_dots(segment: &str) -> Option<String> {
    let trimmed = segment.trim();
    if trimmed.len() < 2 {
        return None;
    }
    if !trimmed.bytes().all(|b| b == b'.') {
        return None;
    }
    // N dots → (N-1) levels up, joined with `/`. `..` = `..`, `...` =
    // `../..`, `....` = `../../..`.
    let levels = trimmed.len() - 1;
    let path = std::iter::repeat("..")
        .take(levels)
        .collect::<Vec<_>>()
        .join("/");
    Some(format!("cd {path}"))
}

/// `Evaluator::expand_interpolation` which substitutes `#{expr}` with
/// `expr`'s evaluated value.
///
/// Used by `dispatch_with_jobs_and_builtins` to make
/// `cd "#{path}"`, `git -C "#{dir}"`, `mv from "#{dst}"`, and the rest
/// of the shell-command surface honor Rush variables — bringing builtin
/// and external-command arg evaluation in line with the documented
/// double-quote interpolation rule.
fn expand_shell_interpolation(segment: &str, evaluator: &mut crate::eval::Evaluator) -> String {
    let mut out = String::with_capacity(segment.len());
    let mut buf = String::new();
    let mut in_single = false;
    let mut in_double = false;
    for c in segment.chars() {
        if c == '\'' && !in_double {
            if !in_single {
                if !buf.is_empty() {
                    out.push_str(&evaluator.expand_interpolation(&buf));
                    buf.clear();
                }
                in_single = true;
                out.push('\'');
            } else {
                in_single = false;
                out.push('\'');
            }
        } else if c == '"' && !in_single {
            in_double = !in_double;
            buf.push('"');
        } else if in_single {
            out.push(c);
        } else {
            buf.push(c);
        }
    }
    if !buf.is_empty() {
        out.push_str(&evaluator.expand_interpolation(&buf));
    }
    out
}

// ── Heredoc Expansion ───────────────────────────────────────────────

/// Process heredocs and backslash-newline continuations in multi-line input.
/// Converts `cmd <<EOF\nbody\nEOF` into `cmd` with heredoc content piped as stdin.
/// Also joins `line \\\nline2` into `line line2`.
fn expand_heredocs(input: &str) -> String {
    let mut result = String::new();
    let lines: Vec<&str> = input.split('\n').collect();
    let mut i = 0;

    while i < lines.len() {
        let mut line = lines[i].to_string();

        // Backslash-newline continuation
        while line.ends_with('\\') && i + 1 < lines.len() {
            line.pop(); // remove trailing backslash
            i += 1;
            line.push_str(lines[i]);
        }

        // Check for heredoc: cmd <<DELIM or cmd <<-DELIM
        if let Some(heredoc_pos) = find_heredoc(&line) {
            let (cmd_part, rest) = line.split_at(heredoc_pos);
            let rest = &rest[2..]; // skip <<
            let strip_tabs = rest.starts_with('-');
            let delim_str = if strip_tabs { &rest[1..] } else { rest };
            let delim = delim_str.trim().trim_matches('\'').trim_matches('"');

            if delim.is_empty() {
                result.push_str(&line);
                result.push('\n');
                i += 1;
                continue;
            }

            // Collect heredoc body
            let mut body = String::new();
            i += 1;
            while i < lines.len() {
                let hline = if strip_tabs {
                    lines[i].trim_start_matches('\t')
                } else {
                    lines[i]
                };
                if hline.trim() == delim {
                    i += 1;
                    break;
                }
                body.push_str(hline);
                body.push('\n');
                i += 1;
            }

            // Write heredoc body to a temp file and redirect stdin
            let tmp = std::env::temp_dir().join(format!("rush_heredoc_{}", std::process::id()));
            if std::fs::write(&tmp, &body).is_ok() {
                let tmp_path = tmp.to_string_lossy();
                result.push_str(cmd_part.trim());
                result.push_str(&format!(" < {tmp_path}"));
                result.push('\n');
            }
            continue;
        }

        result.push_str(&line);
        result.push('\n');
        i += 1;
    }

    // Remove trailing newline
    if result.ends_with('\n') {
        result.pop();
    }
    result
}

/// Find the position of << in a line (not inside quotes).
fn find_heredoc(line: &str) -> Option<usize> {
    let chars: Vec<char> = line.chars().collect();
    let mut in_single = false;
    let mut in_double = false;

    for i in 0..chars.len().saturating_sub(1) {
        if chars[i] == '\'' && !in_double { in_single = !in_single; }
        if chars[i] == '"' && !in_single { in_double = !in_double; }
        if !in_single && !in_double && chars[i] == '<' && chars[i + 1] == '<' {
            // Make sure it's not <<< (herestring)
            if i + 2 < chars.len() && chars[i + 2] == '<' {
                continue;
            }
            return Some(i);
        }
    }
    None
}

// ── Inline Env Vars ─────────────────────────────────────────────────

/// Extract leading VAR=val assignments from a command.
/// `LANG=C sort file` → vars=[("LANG","C")], remaining="sort file"
pub fn extract_inline_env_vars(segment: &str) -> (Vec<(String, String)>, String) {
    // Use the proper shell tokenizer so quoted values like `foo="a b c"`
    // tokenize as one token, not three. The naive split_whitespace
    // version treated `foo="a b c"` as `["foo=\"a", "b", "c\""]` and
    // recorded `foo=a` as the inline var, then ran `b c"` as a command
    // — i.e. every shell-style assignment with a quoted multi-word
    // value was silently broken.
    let words = crate::process::parse_command_line(segment);
    let mut vars = Vec::new();
    let mut cmd_start = 0;

    for word in &words {
        // Check if this word is VAR=VALUE (not ==, not a flag, identifier on left)
        if let Some(eq_pos) = word.find('=') {
            if eq_pos > 0 && !word.starts_with('-') && !word[..eq_pos].contains('(') {
                let left = &word[..eq_pos];
                // Left side must be a valid identifier
                if left.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
                    && left.chars().next().is_some_and(|c| c.is_ascii_alphabetic() || c == '_')
                {
                    // parse_command_line has already stripped the
                    // quoting from the value side, so we don't need
                    // the trim_matches dance.
                    let val = &word[eq_pos + 1..];
                    vars.push((left.to_string(), val.to_string()));
                    cmd_start += 1;
                    continue;
                }
            }
        }
        break; // First non-assignment word = start of command
    }

    if cmd_start == 0 {
        // No inline vars at all — return the original segment as-is.
        return (vars, segment.to_string());
    }
    if cmd_start >= words.len() {
        // Pure assignment(s) with no command. Caller distinguishes by
        // checking for an empty remaining segment. Without this branch,
        // dispatch would re-run the original `foo=val` segment as a
        // command and either crash or print "command not found", which
        // also broke `foo="quoted multiword"` because the leftover
        // contains an unclosed quote.
        return (vars, String::new());
    }

    // Re-quote remaining tokens so any literal whitespace they contain
    // survives downstream re-tokenization. Single-quote unless the
    // value itself contains a single quote, in which case fall back to
    // double quotes (good enough for common cases; this code path is
    // reached only after inline vars and is meant to preserve the
    // command exactly as the user wrote it).
    let remaining: String = words[cmd_start..]
        .iter()
        .map(|w| reshell_quote(w))
        .collect::<Vec<_>>()
        .join(" ");
    (vars, remaining)
}

/// Re-emit a token with shell quoting so a downstream tokenizer sees
/// the same structure. Plain tokens (no whitespace, no shell metachars)
/// are returned as-is. Tokens with whitespace get single-quoted (or
/// double-quoted if they contain a single quote already).
fn reshell_quote(token: &str) -> String {
    let needs_quoting = token.is_empty()
        || token.chars().any(|c| matches!(c,
            ' ' | '\t' | '\n' | '|' | '&' | ';' | '<' | '>' | '(' | ')' | '$' | '`' | '"' | '\'' | '\\'
        ));
    if !needs_quoting {
        return token.to_string();
    }
    if !token.contains('\'') {
        return format!("'{token}'");
    }
    // Escape `\` and `"` for double-quoted form.
    let escaped = token.replace('\\', "\\\\").replace('"', "\\\"");
    format!("\"{escaped}\"")
}

// ── Chain Splitting ─────────────────────────────────────────────────

#[derive(Debug, PartialEq)]
enum ChainOp {
    None,  // first segment or after ;
    And,   // after &&
    Or,    // after ||
    Semi,  // after ;
}

struct ChainSegment {
    command: String,
    operator: ChainOp,
}

/// Split a command line on &&, ||, ; respecting quotes.
fn split_chains(input: &str) -> Vec<ChainSegment> {
    // Rush block openers that are closed by `end`. Needed so that
    // `def foo; body; end` is treated as one chunk, not three.
    const BLOCK_OPENERS: &[&str] = &[
        "def", "class", "enum", "if", "unless", "while", "until",
        "loop", "for", "case", "match", "try", "begin",
        "orchestrate", "parallel", "do",
    ];

    let mut segments = Vec::new();
    let mut current = String::new();
    let mut current_op = ChainOp::None;
    let mut in_single = false;
    let mut in_double = false;
    let mut brace_depth: i32 = 0; // track { } for brace groups
    let mut paren_depth: i32 = 0; // track ( ) for subshells
    let mut block_depth: i32 = 0; // track def/class/...end
    let chars: Vec<char> = input.chars().collect();
    let mut i = 0;

    // Helper: extract the identifier starting at position `start` (if any).
    fn word_at(chars: &[char], start: usize) -> Option<&[char]> {
        if start >= chars.len() { return None; }
        let c = chars[start];
        if !(c.is_ascii_alphabetic() || c == '_') { return None; }
        let mut end = start;
        while end < chars.len() && (chars[end].is_ascii_alphanumeric() || chars[end] == '_') {
            end += 1;
        }
        Some(&chars[start..end])
    }

    // True if position `start` is at a word boundary (start of input
    // or preceded by a non-identifier char).
    fn at_word_boundary(chars: &[char], start: usize) -> bool {
        if start == 0 { return true; }
        let prev = chars[start - 1];
        !(prev.is_ascii_alphanumeric() || prev == '_')
    }

    while i < chars.len() {
        let ch = chars[i];

        if in_single {
            if ch == '\'' { in_single = false; }
            current.push(ch);
            i += 1;
            continue;
        }
        if in_double {
            if ch == '\\' && i + 1 < chars.len() {
                current.push(ch);
                current.push(chars[i + 1]);
                i += 2;
                continue;
            }
            if ch == '"' { in_double = false; }
            current.push(ch);
            i += 1;
            continue;
        }

        if ch == '\'' { in_single = true; current.push(ch); i += 1; continue; }
        if ch == '"' { in_double = true; current.push(ch); i += 1; continue; }

        // Backslash escape: copy `\` + the following char verbatim so
        // the next char doesn't get interpreted as a chain operator.
        // The canonical case is `find ... -exec ... \;` (#296), where
        // `;` would otherwise terminate the segment and find errors out.
        // Trailing `\` at end of input is left as-is.
        if ch == '\\' && i + 1 < chars.len() {
            current.push(ch);
            current.push(chars[i + 1]);
            i += 2;
            continue;
        }

        // Track brace/paren depth for { } and ( )
        if ch == '{' { brace_depth += 1; }
        if ch == '}' { brace_depth -= 1; }
        if ch == '(' { paren_depth += 1; }
        if ch == ')' { paren_depth -= 1; }

        // Track block-keyword depth (def/class/...end) at word boundaries.
        if at_word_boundary(&chars, i) {
            if let Some(w) = word_at(&chars, i) {
                let word: String = w.iter().collect();
                if word == "end" { block_depth -= 1; }
                else if BLOCK_OPENERS.iter().any(|&op| op == word) { block_depth += 1; }
                // Consume the whole word so the first char isn't re-processed.
                for c in w { current.push(*c); }
                i += w.len();
                continue;
            }
        }

        // Don't split on operators inside braces, parens, or blocks.
        if brace_depth > 0 || paren_depth > 0 || block_depth > 0 {
            current.push(ch);
            i += 1;
            continue;
        }

        // Check for && and ||
        if ch == '&' && i + 1 < chars.len() && chars[i + 1] == '&' {
            segments.push(ChainSegment { command: std::mem::take(&mut current), operator: current_op });
            current_op = ChainOp::And;
            i += 2;
            continue;
        }
        if ch == '|' && i + 1 < chars.len() && chars[i + 1] == '|' {
            segments.push(ChainSegment { command: std::mem::take(&mut current), operator: current_op });
            current_op = ChainOp::Or;
            i += 2;
            continue;
        }
        if ch == ';' {
            segments.push(ChainSegment { command: std::mem::take(&mut current), operator: current_op });
            current_op = ChainOp::Semi;
            i += 1;
            continue;
        }

        current.push(ch);
        i += 1;
    }

    if !current.trim().is_empty() {
        segments.push(ChainSegment { command: current, operator: current_op });
    }

    segments
}

// ── Pipeline Execution ──────────────────────────────────────────────

fn run_pipeline(
    evaluator: &mut Evaluator,
    segments: &[String],
    builtins: Option<&mut PipelineBuiltins>,
) -> i32 {
    if segments.is_empty() { return 0; }

    let first = &segments[0];
    let auto_obj = pipeline::should_auto_objectify(first);

    let mut builtins = builtins;

    // Execute first segment
    let first_word = first.split_whitespace().next().unwrap_or("");
    let first_is_builtin = builtins
        .as_deref_mut()
        .map(|b| !first_word.is_empty() && (b.is_builtin)(first_word))
        .unwrap_or(false);

    let value = if first_is_builtin {
        // LHS builtin in a Rush-pipe-op pipeline. Capture its pipe-
        // friendly output via fd-redirect and convert to an array of
        // lines for downstream pipe ops / builtins.
        let b = builtins.as_deref_mut().expect("first_is_builtin implies Some");
        let args = first.trim_start().strip_prefix(first_word).unwrap_or("").trim_start();
        let bytes = capture_builtin_stdout(b, first_word, args, &[]);
        let text = String::from_utf8_lossy(&bytes).into_owned();
        let text_val = pipeline::text_to_array(&text);
        if auto_obj {
            pipeline::apply_pipe_op(text_val, &pipeline::parse_pipe_op("objectify"))
        } else {
            text_val
        }
    } else if !triage::is_rush_syntax(first) {
        // Before routing to shell, check whether the first segment is
        // actually a Rush value expression that triage conservatively
        // classified as "shell." Covers bare Rush variable refs
        // (`arr | where …`), string literals (`"text" | count`),
        // command substitutions (`$(…) | op`), and any expression-shape
        // the parser recognizes. Unlocks the #249 sketch form — value
        // sources can now start a pipeline. (#265 Phase 1.)
        if let Some(val) = try_eval_as_value_source(first, evaluator) {
            val
        } else {
            let result = process::run_native_capture(first);
            if !result.stderr.is_empty() {
                eprintln!("{}", result.stderr);
            }
            let text_val = pipeline::text_to_array(&result.stdout);
            if auto_obj {
                pipeline::apply_pipe_op(text_val, &pipeline::parse_pipe_op("objectify"))
            } else {
                text_val
            }
        }
    } else {
        match parser::parse(first) {
            Ok(nodes) => evaluator.exec_toplevel(&nodes).unwrap_or(Value::Nil),
            Err(_) => {
                let result = process::run_native_capture(first);
                let text_val = pipeline::text_to_array(&result.stdout);
                if auto_obj {
                    pipeline::apply_pipe_op(text_val, &pipeline::parse_pipe_op("objectify"))
                } else {
                    text_val
                }
            }
        }
    };

    run_pipeline_from_value(evaluator, value, &segments[1..], builtins)
}

/// If `segment` parses as a single Rush expression that yields a value,
/// evaluate it and return the value. Used to route things like
/// `arr | where …`, `"hello" | count`, or `$(cmd) | op` into the
/// value-pipeline path even though `triage::is_rush_syntax` returns
/// false for those shapes (the heuristic is deliberately conservative).
///
/// Returns `None` when:
///   - the segment doesn't parse
///   - it parses to zero or multiple top-level nodes
///   - the single node is a bare variable reference to a name that
///     isn't currently bound in the Rush scope (falling through to
///     shell dispatch matches prior behavior for unknown names)
///   - the node is a shape that could be a command (function calls
///     with no parens, etc.) — we stay conservative here; the parser
///     has specific patterns it accepts for call shapes, so recognized
///     expression nodes are the green-path set below.
fn try_eval_as_value_source(
    segment: &str,
    evaluator: &mut Evaluator,
) -> Option<Value> {
    use crate::ast::Node;

    let nodes = parser::parse(segment).ok()?;
    if nodes.len() != 1 {
        return None;
    }

    // Unambiguous value-expression shapes. BinaryOp / UnaryOp / Ternary
    // are deliberately excluded — `df -h` parses as `df - h` (BinaryOp)
    // and `!file` as UnaryOp, both of which are shell-command shapes the
    // parser just happens to accept. Users who genuinely want arithmetic
    // LHS can assign to a variable first: `x = 1 + 2; x | op`.
    let is_value_expr = matches!(
        &nodes[0],
        Node::Array { .. }
            | Node::Hash { .. }
            | Node::Literal { .. }           // numbers, strings, true/false, nil
            | Node::InterpolatedString { .. }
            | Node::Range { .. }
            | Node::Symbol { .. }
            | Node::RegexLiteral { .. }
            | Node::CommandSub { .. }
            | Node::MethodCall { .. }
            | Node::FunctionCall { .. }
            | Node::PropertyAccess { .. }
            | Node::SafeNav { .. }
    );

    if is_value_expr {
        return evaluator.exec_toplevel(&nodes).ok();
    }

    // Bare variable reference — only route as value if the name is
    // currently bound. An unbound identifier should fall through to
    // shell dispatch so `foo | bar` still runs `foo` on PATH.
    if let Node::VariableRef { name } = &nodes[0] {
        if evaluator.env.get(name).is_some() {
            return evaluator.exec_toplevel(&nodes).ok();
        }
    }

    None
}

/// Render a Value as the text that will be written to a child process's
/// stdin (or passed to a builtin's `handle_pipe`). The rules are
/// deliberately shell-friendly so tools like `jq`, `grep`, `awk`, and
/// `sort` can consume the upstream Value without extra conversion:
///
///   * Scalars (nil, bool, int, float, string, symbol) → the same text
///     `puts` would print. Strings are not JSON-quoted; callers who
///     want quoting should bridge with `| as json`.
///   * Ranges → `start..end` / `start...end` text form.
///   * Arrays of scalars → one element per line (newline-joined). The
///     classic Unix line-per-record shape.
///   * Arrays of hashes or arrays → one JSON document per line (JSONL /
///     NDJSON). Tools like `jq -c` consume this directly; `awk` can
///     still see per-record lines.
///   * Hashes → single-line JSON. Consumers get valid JSON for `jq`.
///
/// Pre-#265-Phase-2 behavior stringified hashes via Rush's inspect form
/// (`{a: 1, b: "hi"}`) which looked like JSON but wasn't — no quoted
/// keys, symbols rendered as `:name`. Shell tools rejected it. The new
/// rules keep scalar and scalar-array behavior identical (those were
/// already right) and switch structured data to real JSON.
fn format_value_for_stdin(value: &Value) -> String {
    match value {
        Value::Array(items) => {
            // JSON lines when any element is structured; newline-joined
            // rush_string otherwise. Avoids surprising mixed cases by
            // falling to JSON for the whole array if one element needs it.
            let needs_json = items
                .iter()
                .any(|v| matches!(v, Value::Hash(_) | Value::Array(_)));
            if needs_json {
                items
                    .iter()
                    .map(|v| crate::mcp_client::value_to_json(v).to_string())
                    .collect::<Vec<_>>()
                    .join("\n")
            } else {
                items
                    .iter()
                    .map(|v| v.to_rush_string())
                    .collect::<Vec<_>>()
                    .join("\n")
            }
        }
        Value::Hash(_) => crate::mcp_client::value_to_json(value).to_string(),
        other => other.to_rush_string(),
    }
}

/// Apply the remaining pipeline segments to a pre-computed initial
/// value. Split out of `run_pipeline` so the LHS-builtin path in #236
/// can capture a builtin's output into a Value and thread it through
/// the rest of the pipeline. Middle/terminal rush-cli builtins (#238)
/// are routed via the supplied `PipelineBuiltins` so `ls | alias | head`
/// and `hostname | alias | first 1` stop failing with 127.
fn run_pipeline_from_value(
    evaluator: &mut Evaluator,
    mut value: Value,
    rest_segments: &[String],
    mut builtins: Option<&mut PipelineBuiltins>,
) -> i32 {
    if rest_segments.is_empty() {
        let output = value.to_rush_string();
        if !output.is_empty() {
            println!("{output}");
        }
        return evaluator.exit_code;
    }

    let last_idx = rest_segments.len() - 1;

    for (i, segment) in rest_segments.iter().enumerate() {
        let first_word = segment.split_whitespace().next().unwrap_or("");
        let is_last = i == last_idx;

        // Rush pipe op (where, sort, first, puts, …) — pure value flow.
        if pipeline::is_pipe_op(first_word) {
            let op = pipeline::parse_pipe_op(segment);
            value = pipeline::apply_pipe_op(value, &op);
            continue;
        }

        // Rush-cli builtin stage: consume the current value as stdin
        // bytes and replace with the builtin's output (or, if it's the
        // terminal stage, let its output go straight to the user).
        let is_rush_builtin = builtins
            .as_deref_mut()
            .map(|b| !first_word.is_empty() && (b.is_builtin)(first_word))
            .unwrap_or(false);
        if is_rush_builtin {
            let b = builtins.as_deref_mut().expect("is_rush_builtin implies Some");
            let args = segment
                .trim_start()
                .strip_prefix(first_word)
                .unwrap_or("")
                .trim_start();
            let stdin_text = format_value_for_stdin(&value);
            if is_last {
                // Terminal builtin: call directly so its output reaches
                // real stdout instead of being captured and reserialized
                // through `Array.to_rush_string()`.
                let code = (b.handle_pipe)(first_word, args, stdin_text.as_bytes())
                    .unwrap_or_else(|| {
                        eprintln!("rush: {first_word}: builtin does not consume stdin");
                        1
                    });
                evaluator.exit_code = code;
                value = Value::Nil;
                continue;
            }
            // Middle builtin: capture so downstream stages can use it.
            let captured = capture_builtin_stdout(b, first_word, args, stdin_text.as_bytes());
            let text = String::from_utf8_lossy(&captured).into_owned();
            value = pipeline::text_to_array(&text);
            continue;
        }

        // Shell segment — pipe current value's stdin text into the
        // child and read its stdout back as the new value.
        let input_text = format_value_for_stdin(&value);
        let parts = process::parse_command_line(segment);
        if parts.is_empty() {
            continue;
        }
        let args: Vec<&str> = parts[1..].iter().map(|s| s.as_str()).collect();
        match std::process::Command::new(&parts[0])
            .args(&args)
            .stdin(std::process::Stdio::piped())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::inherit())
            .spawn()
        {
            Ok(mut child) => {
                use std::io::Write;
                if let Some(mut stdin) = child.stdin.take() {
                    stdin.write_all(input_text.as_bytes()).ok();
                }
                match child.wait_with_output() {
                    Ok(output) => {
                        value = Value::String(
                            String::from_utf8_lossy(&output.stdout).trim_end().to_string(),
                        );
                    }
                    Err(_) => {
                        value = Value::String(String::new());
                    }
                }
            }
            Err(e) => {
                eprintln!("rush: {}: {e}", parts[0]);
                value = Value::String(String::new());
            }
        }
    }

    let output = value.to_rush_string();
    if !output.is_empty() {
        println!("{output}");
    }

    evaluator.exit_code
}

/// True if this multi-segment pipeline needs Rush's value-flow pipeline
/// (`run_pipeline`) rather than the native pipe chain — i.e. any segment
/// is a Rush pipe op, or any segment is a rush-cli builtin that can't
/// be execve'd.
///
/// All positions are scanned. Index 0 usually isn't a pipe op in top-
/// level dispatch (it's the first command), but `run_pipeline_from_value`
/// calls this helper on the *remaining* slice after an LHS-builtin
/// capture — where index 0 of `remaining` may very well be a pipe op
/// or another builtin. The top-level LHS-builtin fast path checks
/// `is_builtin` on `pipe_segments[0]` before calling this predicate,
/// so we never double-handle that case.
fn segments_need_rush_pipeline(
    segments: &[String],
    builtins: Option<&PipelineBuiltins>,
) -> bool {
    for seg in segments {
        let fw = seg.split_whitespace().next().unwrap_or("");
        if pipeline::is_pipe_op(fw) {
            return true;
        }
        if let Some(b) = builtins {
            if !fw.is_empty() && (b.is_builtin)(fw) {
                return true;
            }
        }
    }
    false
}

/// Capture stdout from a single rush-cli builtin invocation by redirecting
/// fd 1 through a pipe. `upstream_stdin` is passed straight through to
/// `handle_pipe` — empty for an LHS builtin (#236), non-empty when the
/// builtin is a middle/RHS stage consuming upstream bytes (#238).
/// Stderr is left untouched so real error messages still reach the user.
/// Returns the captured bytes (empty on error or on Windows, where fd
/// redirection isn't wired up yet).
#[cfg(unix)]
fn capture_builtin_stdout(
    builtins: &mut PipelineBuiltins,
    name: &str,
    args: &str,
    upstream_stdin: &[u8],
) -> Vec<u8> {
    use std::io::Read;
    use std::os::unix::io::{FromRawFd, RawFd};
    use std::sync::Mutex;

    // fd 1 is process-global — serialize concurrent captures.
    static FD_LOCK: Mutex<()> = Mutex::new(());
    let _guard = FD_LOCK.lock().unwrap_or_else(|p| p.into_inner());

    let mut fds = [0i32; 2];
    if unsafe { libc::pipe(fds.as_mut_ptr()) } != 0 {
        return Vec::new();
    }
    let (r, w): (RawFd, RawFd) = (fds[0], fds[1]);

    use std::io::Write;
    let _ = std::io::stdout().flush();

    let saved = unsafe { libc::dup(1) };
    if saved < 0 {
        unsafe { libc::close(r); libc::close(w); }
        return Vec::new();
    }
    unsafe {
        libc::dup2(w, 1);
        libc::close(w);
    }

    // Drainer thread so the pipe buffer can't deadlock a chatty builtin.
    let reader = unsafe { std::fs::File::from_raw_fd(r) };
    let handle = std::thread::spawn(move || {
        let mut buf = Vec::new();
        let mut rd = reader;
        let _ = rd.read_to_end(&mut buf);
        buf
    });

    let _code = (builtins.handle_pipe)(name, args, upstream_stdin);

    let _ = std::io::stdout().flush();
    unsafe {
        libc::dup2(saved, 1);
        libc::close(saved);
    }

    handle.join().unwrap_or_default()
}

#[cfg(not(unix))]
fn capture_builtin_stdout(
    builtins: &mut PipelineBuiltins,
    name: &str,
    args: &str,
    upstream_stdin: &[u8],
) -> Vec<u8> {
    // Windows: fd redirection requires SetStdHandle / CreatePipe; skip
    // for now. The builtin writes directly to the console, which is at
    // least no worse than the pre-fix 127 behavior.
    let _ = (builtins.handle_pipe)(name, args, upstream_stdin);
    Vec::new()
}

/// Spawn a native pipe chain (`cmd1 | cmd2 | ...`) with the first
/// stage's stdin fed from `initial_bytes`. Last stage's stdout inherits
/// the terminal. Used after an LHS rush-cli builtin writes into a pipe.
fn run_native_chain_with_initial_bytes(
    segments: &[String],
    initial_bytes: &[u8],
) -> i32 {
    use std::io::Write;
    use std::process::{Command, Stdio};

    if segments.is_empty() { return 0; }

    let commands: Vec<Vec<String>> = segments
        .iter()
        .map(|s| process::parse_command_line(s.trim()))
        .collect();

    let mut prev_stdout: Option<std::process::ChildStdout> = None;
    let mut children: Vec<std::process::Child> = Vec::new();
    let mut first_stdin: Option<std::process::ChildStdin> = None;

    for (i, parts) in commands.iter().enumerate() {
        if parts.is_empty() { continue; }
        let is_last = i == commands.len() - 1;

        let stdin = if i == 0 {
            Stdio::piped()
        } else if let Some(prev) = prev_stdout.take() {
            Stdio::from(prev)
        } else {
            Stdio::inherit()
        };
        let stdout = if is_last { Stdio::inherit() } else { Stdio::piped() };

        let program = &parts[0];
        let args: Vec<&str> = parts[1..].iter().map(String::as_str).collect();

        match Command::new(program)
            .args(&args)
            .stdin(stdin)
            .stdout(stdout)
            .stderr(Stdio::inherit())
            .spawn()
        {
            Ok(mut child) => {
                if i == 0 {
                    first_stdin = child.stdin.take();
                }
                if !is_last {
                    prev_stdout = child.stdout.take();
                }
                children.push(child);
            }
            Err(e) => {
                eprintln!("rush: {program}: {e}");
                for mut c in children {
                    let _ = c.kill();
                    let _ = c.wait();
                }
                return 127;
            }
        }
    }

    // Write the captured upstream bytes to the first stage's stdin on a
    // separate thread so we don't deadlock if the chain produces back-
    // pressure. Dropping the handle closes the pipe, signaling EOF.
    if let Some(mut sin) = first_stdin.take() {
        let bytes = initial_bytes.to_vec();
        std::thread::spawn(move || {
            let _ = sin.write_all(&bytes);
        });
    }

    let mut last_code = 0;
    for mut child in children {
        match child.wait() {
            Ok(status) => last_code = status.code().unwrap_or(-1),
            Err(_) => last_code = -1,
        }
    }
    last_code
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::eval::{Evaluator, Output};

    struct TestOutput { lines: Vec<String> }
    impl TestOutput { fn new() -> Self { Self { lines: Vec::new() } } }
    impl Output for TestOutput {
        fn puts(&mut self, s: &str) { self.lines.push(s.to_string()); }
        fn print(&mut self, s: &str) { self.lines.push(s.to_string()); }
        fn warn(&mut self, s: &str) { self.lines.push(format!("WARN: {s}")); }
    }

    fn run(input: &str) -> (i32, Vec<String>) {
        let mut output = TestOutput::new();
        let result = {
            let mut eval = Evaluator::new(&mut output);
            dispatch(input, &mut eval, None)
        };
        (result.exit_code, output.lines)
    }

    // ── Chain operators ─────────────────────────────────────────────

    #[test]
    fn chain_and_success() {
        let (_, lines) = run("puts \"a\"; puts \"b\"");
        assert_eq!(lines, vec!["a", "b"]);
    }

    #[test]
    fn chain_semicolon() {
        let (_, lines) = run("x = 1; y = 2; puts x + y");
        assert_eq!(lines, vec!["3"]);
    }

    #[test]
    fn chain_and_with_shell_false() {
        // /usr/bin/false returns exit code 1
        let (code, lines) = run("/usr/bin/false && puts \"no\"");
        assert_ne!(code, 0);
        assert!(lines.is_empty()); // puts should not run
    }

    #[test]
    fn chain_or_with_shell_false() {
        let (_, lines) = run("/usr/bin/false || puts \"yes\"");
        assert_eq!(lines, vec!["yes"]);
    }

    // ── Triage ──────────────────────────────────────────────────────

    #[test]
    fn rush_syntax_dispatched() {
        let (_, lines) = run("puts 1 + 2");
        assert_eq!(lines, vec!["3"]);
    }

    #[test]
    fn shell_command_dispatched() {
        let (code, _) = run("echo hello");
        assert_eq!(code, 0);
    }

    #[test]
    fn assignment_is_rush() {
        let (_, lines) = run("x = 42; puts x");
        assert_eq!(lines, vec!["42"]);
    }

    // ── Split chains ────────────────────────────────────────────────

    #[test]
    fn split_chains_basic() {
        let chains = split_chains("a && b || c ; d");
        assert_eq!(chains.len(), 4);
        assert_eq!(chains[0].command.trim(), "a");
        assert_eq!(chains[1].operator, ChainOp::And);
        assert_eq!(chains[2].operator, ChainOp::Or);
        assert_eq!(chains[3].operator, ChainOp::Semi);
    }

    #[test]
    fn split_chains_quoted() {
        let chains = split_chains("echo \"a && b\" ; echo c");
        assert_eq!(chains.len(), 2);
        assert!(chains[0].command.contains("a && b"));
    }

    // ── #296: backslash-escaped chain operators ─────────────────────

    #[test]
    fn split_chains_backslash_escaped_semicolon_kept_in_segment() {
        // The canonical `find -exec` form. Without the escape arm,
        // rush splits on the bare `;` and find errors with
        // "missing argument to `-exec'".
        let chains = split_chains(r"find . -exec ls {} \; ; echo done");
        assert_eq!(chains.len(), 2);
        assert!(chains[0].command.contains(r"\;"));
        assert!(chains[0].command.contains("-exec ls"));
        assert_eq!(chains[1].command.trim(), "echo done");
    }

    #[test]
    fn split_chains_single_quoted_semicolon_kept() {
        // Alternative form. Single-quote arm should already preserve.
        let chains = split_chains(r"find . -exec ls {} ';' ; echo done");
        assert_eq!(chains.len(), 2);
        assert!(chains[0].command.contains("';'"));
        assert_eq!(chains[1].command.trim(), "echo done");
    }

    #[test]
    fn split_chains_backslash_escaped_ampersand_kept() {
        // Pathological but symmetric: `\&\&` shouldn't be parsed as
        // a chain `&&`. (No real-world idiom uses this; included for
        // consistency with the escape rule.)
        let chains = split_chains(r"echo a \&\& echo b");
        assert_eq!(chains.len(), 1);
        assert!(chains[0].command.contains(r"\&\&"));
    }

    #[test]
    fn split_chains_trailing_backslash_left_alone() {
        // A bare `\` at end of input shouldn't panic; just keep it.
        let chains = split_chains(r"echo hi \");
        assert_eq!(chains.len(), 1);
        assert!(chains[0].command.contains(r"\"));
    }

    // ── Exit ────────────────────────────────────────────────────────

    #[test]
    fn exit_sets_flag() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        let result = dispatch("exit", &mut eval, None);
        assert!(result.should_exit);
    }

    // ── Inline env vars ─────────────────────────────────────────────

    #[test]
    fn extract_inline_vars() {
        let (vars, cmd) = extract_inline_env_vars("LANG=C sort file.txt");
        assert_eq!(vars, vec![("LANG".to_string(), "C".to_string())]);
        assert_eq!(cmd, "sort file.txt");
    }

    #[test]
    fn extract_multiple_inline_vars() {
        let (vars, cmd) = extract_inline_env_vars("FOO=1 BAR=2 cmd");
        assert_eq!(vars.len(), 2);
        assert_eq!(cmd, "cmd");
    }

    #[test]
    fn extract_no_inline_vars() {
        let (vars, cmd) = extract_inline_env_vars("ls -la");
        assert!(vars.is_empty());
        assert_eq!(cmd, "ls -la");
    }

    #[test]
    fn extract_assignment_not_inline() {
        // "x=5" alone is an assignment, not an inline var
        let (vars, cmd) = extract_inline_env_vars("x=5");
        assert_eq!(vars.len(), 1);
        // Pure-assignment case returns an empty remainder so dispatch
        // doesn't re-run the original `x=5` as a command.
        assert!(cmd.is_empty(), "pure assignment must return empty remainder, got {cmd:?}");
    }

    #[test]
    fn extract_inline_quoted_multiword_value() {
        // The whole point of switching extract_inline_env_vars to use
        // parse_command_line: `foo="a b c"` must be one assignment, not
        // three split tokens (which would yield foo=a + leftover `b c"`
        // as a bogus command). #298 was the ticket.
        let (vars, cmd) = extract_inline_env_vars(r#"foo="a b c""#);
        assert_eq!(vars, vec![("foo".to_string(), "a b c".to_string())]);
        assert!(cmd.is_empty());
    }

    #[test]
    fn extract_inline_quoted_with_following_command() {
        let (vars, cmd) = extract_inline_env_vars(r#"FOO="hello world" echo done"#);
        assert_eq!(vars, vec![("FOO".to_string(), "hello world".to_string())]);
        // Command leftover must survive re-quote so the downstream
        // tokenizer sees the original structure.
        assert_eq!(cmd, "echo done");
    }

    // ── Brace group ─────────────────────────────────────────────────

    #[test]
    fn brace_group_single() {
        let (_, lines) = run("{ puts \"hello\" }");
        assert_eq!(lines, vec!["hello"]);
    }

    // ── set -x ──────────────────────────────────────────────────────

    #[test]
    fn set_xtrace() {
        use crate::flags;
        flags::set_xtrace(false); // ensure clean state
        let (_, _) = run("puts \"hello\"");
        // No xtrace output expected
        flags::set_xtrace(false);
    }

    // ── cd: quoted / escaped path arguments (#231) ───────────────────

    // std::env::set_current_dir mutates process-global state; serialize
    // cwd-mutating tests to avoid interleaving with parallel test runs.
    static CWD_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    fn with_cd_in<F: FnOnce()>(path: &std::path::Path, f: F) {
        let _guard = CWD_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let saved = std::env::current_dir().unwrap();
        f();
        let actual = std::env::current_dir().unwrap();
        let want = path.canonicalize().unwrap();
        let got = actual.canonicalize().unwrap_or(actual);
        assert_eq!(got, want, "expected cwd to be {want:?}, got {got:?}");
        std::env::set_current_dir(saved).ok();
    }

    fn fresh_space_dir(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("rush cd test {tag} {}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn cd_double_quoted_path_with_space() {
        let dir = fresh_space_dir("dq");
        with_cd_in(&dir, || {
            let (code, _) = run(&format!("cd \"{}\"", dir.display()));
            assert_eq!(code, 0);
        });
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn cd_single_quoted_path_with_space() {
        let dir = fresh_space_dir("sq");
        with_cd_in(&dir, || {
            let (code, _) = run(&format!("cd '{}'", dir.display()));
            assert_eq!(code, 0);
        });
        let _ = std::fs::remove_dir_all(&dir);
    }

    // Backslash-escaped spaces are a POSIX convention; on Windows
    // users quote paths instead, and bare `\` is a path separator.
    #[test]
    #[cfg(unix)]
    fn cd_backslash_escaped_space() {
        let dir = fresh_space_dir("esc");
        with_cd_in(&dir, || {
            let escaped = dir.display().to_string().replace(' ', "\\ ");
            let (code, _) = run(&format!("cd {escaped}"));
            assert_eq!(code, 0);
        });
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn cd_plain_path_still_works() {
        let _guard = CWD_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let saved = std::env::current_dir().unwrap();
        let dir = std::env::temp_dir();
        let (code, _) = run(&format!("cd {}", dir.display()));
        assert_eq!(code, 0);
        std::env::set_current_dir(saved).ok();
    }

    #[test]
    fn cd_no_arg_goes_home() {
        let _guard = CWD_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let saved = std::env::current_dir().unwrap();
        let home = std::env::var("HOME").unwrap_or_default();
        if !home.is_empty() {
            let (code, _) = run("cd");
            assert_eq!(code, 0);
            let actual = std::env::current_dir().unwrap();
            assert_eq!(
                actual.canonicalize().unwrap_or(actual),
                std::path::PathBuf::from(&home).canonicalize().unwrap(),
            );
        }
        std::env::set_current_dir(saved).ok();
    }

    // ── Bare-dot shortcut: .. → cd .., ... → cd ../.., etc. ─────────

    #[test]
    fn expand_bare_dots_double() {
        assert_eq!(expand_bare_dots(".."), Some("cd ..".to_string()));
    }

    #[test]
    fn expand_bare_dots_triple() {
        assert_eq!(expand_bare_dots("..."), Some("cd ../..".to_string()));
    }

    #[test]
    fn expand_bare_dots_quadruple() {
        assert_eq!(expand_bare_dots("...."), Some("cd ../../..".to_string()));
    }

    #[test]
    fn expand_bare_dots_with_surrounding_whitespace() {
        // The line goes through trim/split before reaching us, but
        // tolerating whitespace on either side keeps behavior stable
        // if a caller passes a sloppy segment.
        assert_eq!(expand_bare_dots("  ..  "), Some("cd ..".to_string()));
    }

    #[test]
    fn expand_bare_dots_single_dot_is_not_expanded() {
        // POSIX `.` is "source"; rewriting it would shadow that and
        // the no-op cd-here behavior. Stay out of the way.
        assert_eq!(expand_bare_dots("."), None);
    }

    #[test]
    fn expand_bare_dots_mixed_chars_not_expanded() {
        assert_eq!(expand_bare_dots(".foo"), None);
        assert_eq!(expand_bare_dots("..rs"), None);
        assert_eq!(expand_bare_dots("cd .."), None);
        assert_eq!(expand_bare_dots(".. ."), None);
    }

    #[test]
    fn bare_dots_actually_change_directory() {
        let _guard = CWD_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let saved = std::env::current_dir().unwrap();
        // Start somewhere with a known parent.
        let tmp = std::env::temp_dir();
        std::env::set_current_dir(&tmp).expect("cd to temp");

        let (code, _) = run("..");
        assert_eq!(code, 0);
        let now = std::env::current_dir().unwrap();
        let expected = tmp.parent().unwrap_or(&tmp);
        assert_eq!(
            now.canonicalize().unwrap_or(now),
            expected.canonicalize().unwrap_or_else(|_| expected.to_path_buf()),
        );
        std::env::set_current_dir(saved).ok();
    }

    // ── #280: bare function calls without parens ────────────────────

    #[test]
    fn bare_zero_arg_function_call_invokes_function() {
        let (_, lines) = run("def f(); puts \"hi\"; end; f");
        assert_eq!(lines, vec!["hi"]);
    }

    #[test]
    fn bare_function_call_passes_args() {
        let (_, lines) = run("def hi(name); puts \"hello #{name}\"; end; hi world");
        assert_eq!(lines, vec!["hello world"]);
    }

    #[test]
    fn bare_function_call_passes_quoted_args() {
        let (_, lines) = run(
            "def show(s); puts s; end; show \"with spaces\"",
        );
        assert_eq!(lines, vec!["with spaces"]);
    }

    #[test]
    fn bare_function_shadows_path_lookup() {
        // A defined function takes precedence over PATH — calling
        // `ls` invokes the function, not `/usr/bin/ls`.
        let (_, lines) = run("def ls(); puts \"shadowed\"; end; ls");
        assert_eq!(lines, vec!["shadowed"]);
    }

    #[test]
    #[cfg(unix)]
    fn unknown_command_still_falls_through_to_path() {
        // No function defined → external command path → PATH lookup
        // → `command not found` (exit 127). Important: the function
        // table check must not swallow PATH dispatch for non-matches.
        // Unix-only: Windows falls back via cmd.exe /C, which returns
        // exit 1/9009 instead of the POSIX 127 convention. See #290.
        let (code, _) = run("definitely_not_a_command_or_function_xyz");
        assert_eq!(code, 127);
    }

    // ── #272: shell-segment `#{...}` interpolation ───────────────────

    #[test]
    #[cfg(unix)]
    fn cd_interpolates_double_quoted_var() {
        // Unix-only: end-to-end cwd comparison is fragile on Windows under
        // canonicalize / 8.3 short-name normalization. The interpolation
        // logic itself is already covered cross-platform by
        // shell_command_interpolates_in_double_quotes / shell_segment_*.
        let _guard = CWD_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let saved = std::env::current_dir().unwrap();
        let target = std::env::temp_dir();
        let line = format!("dir = \"{}\"; cd \"#{{dir}}\"", target.display());
        let (code, _) = run(&line);
        assert_eq!(code, 0);
        let now = std::env::current_dir().unwrap();
        assert_eq!(
            now.canonicalize().unwrap_or(now),
            target.canonicalize().unwrap(),
        );
        std::env::set_current_dir(saved).ok();
    }

    #[test]
    fn cd_interpolates_unquoted_var() {
        let _guard = CWD_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let saved = std::env::current_dir().unwrap();
        let target = std::env::temp_dir();
        let line = format!("dir = \"{}\"; cd #{{dir}}", target.display());
        let (code, _) = run(&line);
        assert_eq!(code, 0);
        std::env::set_current_dir(saved).ok();
    }

    #[test]
    fn shell_command_interpolates_in_double_quotes() {
        // Directly test the helper rather than spawning a process —
        // run_native captures real output we don't want to depend on.
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        let _ = dispatch("name = \"world\"", &mut eval, None);
        let expanded =
            expand_shell_interpolation("/bin/echo \"hello #{name}\"", &mut eval);
        assert_eq!(expanded, "/bin/echo \"hello world\"");
    }

    #[test]
    fn shell_segment_single_quotes_stay_literal() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        let _ = dispatch("name = \"world\"", &mut eval, None);
        // Single-quoted region: literal `#{name}` preserved.
        let expanded =
            expand_shell_interpolation("echo 'literal #{name}'", &mut eval);
        assert_eq!(expanded, "echo 'literal #{name}'");
    }

    #[test]
    fn shell_segment_mixed_quotes() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        let _ = dispatch("x = \"X\"", &mut eval, None);
        let expanded = expand_shell_interpolation(
            "echo \"dq #{x}\" 'sq #{x}' #{x}",
            &mut eval,
        );
        assert_eq!(expanded, "echo \"dq X\" 'sq #{x}' X");
    }

    #[test]
    fn shell_segment_unterminated_interp_is_left_literal() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        let expanded = expand_shell_interpolation("echo \"#{never\"", &mut eval);
        assert_eq!(expanded, "echo \"#{never\"");
    }

    #[test]
    fn shell_segment_apostrophe_inside_double_quotes() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        let _ = dispatch("x = \"V\"", &mut eval, None);
        // The inner `'` must NOT toggle single-quote mode while in_double.
        let expanded =
            expand_shell_interpolation("echo \"He said 'hi #{x}'\"", &mut eval);
        assert_eq!(expanded, "echo \"He said 'hi V'\"");
    }

    // ── PipelineBuiltins plumbing (#224-A) ───────────────────────────

    /// Helper: dispatch a line with a test PipelineBuiltins that records
    /// invocations. Returns (exit_code, invocations). Since #229 moved
    /// `$?` to a thread-local, this no longer needs the RUSH_LAST_EXIT
    /// mutex — each test thread has its own last-exit cell.
    fn run_with_pipeline_builtins(
        line: &str,
        names: &[&str],
        results: std::collections::HashMap<String, Option<i32>>,
    ) -> (i32, Vec<(String, String, Vec<u8>)>) {
        use std::cell::RefCell;
        let calls: RefCell<Vec<(String, String, Vec<u8>)>> = RefCell::new(Vec::new());
        let names_owned: Vec<String> = names.iter().map(|s| s.to_string()).collect();

        let mut output = TestOutput::new();
        let exit = {
            let mut eval = Evaluator::new(&mut output);
            let is_builtin = |n: &str| names_owned.iter().any(|x| x == n);
            let mut handle_pipe = |name: &str, args: &str, stdin: &[u8]| -> Option<i32> {
                calls.borrow_mut().push((name.to_string(), args.to_string(), stdin.to_vec()));
                results.get(name).copied().flatten()
            };
            let mut pb = PipelineBuiltins {
                is_builtin: &is_builtin,
                handle_pipe: &mut handle_pipe,
            };
            dispatch_with_jobs_and_builtins(line, &mut eval, None, Some(&mut pb)).exit_code
        };
        (exit, calls.into_inner())
    }

    #[test]
    fn pipe_rhs_builtin_receives_stdin() {
        let mut results = std::collections::HashMap::new();
        results.insert("fakebuiltin".to_string(), Some(0));
        let (code, calls) = run_with_pipeline_builtins(
            "echo hello | fakebuiltin --flag arg",
            &["fakebuiltin"],
            results,
        );
        assert_eq!(code, 0);
        assert_eq!(calls.len(), 1);
        let (name, args, stdin) = &calls[0];
        assert_eq!(name, "fakebuiltin");
        assert_eq!(args, "--flag arg");
        // echo outputs "hello\n"
        assert_eq!(std::str::from_utf8(stdin).unwrap().trim(), "hello");
    }

    #[test]
    fn pipe_rhs_builtin_that_rejects_stdin_gets_clear_error() {
        // handle_pipe returns None → dispatch should report exit 1 and
        // emit "does not consume stdin" (stderr isn't captured here,
        // but we can confirm the non-127 exit code).
        let mut results = std::collections::HashMap::new();
        results.insert("readonlybuiltin".to_string(), None);
        let (code, calls) = run_with_pipeline_builtins(
            "echo x | readonlybuiltin",
            &["readonlybuiltin"],
            results,
        );
        assert_eq!(code, 1);
        assert_eq!(calls.len(), 1);
    }

    #[test]
    fn pipe_rhs_non_builtin_falls_through_to_native() {
        // is_builtin returns false → no handle_pipe call, native pipeline
        // runs and succeeds (cat exists on PATH).
        let (_code, calls) = run_with_pipeline_builtins(
            "echo x | cat",
            &[],
            std::collections::HashMap::new(),
        );
        assert!(calls.is_empty(), "handle_pipe should not be called for non-builtins");
    }

    // ── LHS-builtin routing (#236) ───────────────────────────────────

    #[test]
    fn pipe_lhs_builtin_handle_pipe_called_with_empty_stdin() {
        // `fakebuiltin arg1 | cat` — LHS is a builtin, so dispatch
        // should call handle_pipe with empty stdin (capturing its
        // output) and feed the capture into the native tail (cat).
        let mut results = std::collections::HashMap::new();
        results.insert("fakebuiltin".to_string(), Some(0));
        let (_code, calls) = run_with_pipeline_builtins(
            "fakebuiltin arg1 | cat",
            &["fakebuiltin"],
            results,
        );
        assert_eq!(calls.len(), 1, "handle_pipe should be called exactly once for LHS");
        let (name, args, stdin) = &calls[0];
        assert_eq!(name, "fakebuiltin");
        assert_eq!(args, "arg1");
        assert!(stdin.is_empty(), "LHS builtin receives empty stdin, got {stdin:?}");
    }

    #[test]
    fn pipe_lhs_builtin_into_rush_pipe_op() {
        // `fakebuiltin | first 2` — LHS is a builtin, downstream uses a
        // Rush pipe op. Handler must still be called (to capture),
        // and dispatch shouldn't attempt to execve `fakebuiltin`.
        let mut results = std::collections::HashMap::new();
        results.insert("fakebuiltin".to_string(), Some(0));
        let (_code, calls) = run_with_pipeline_builtins(
            "fakebuiltin | first 2",
            &["fakebuiltin"],
            results,
        );
        assert_eq!(calls.len(), 1);
        assert_eq!(calls[0].0, "fakebuiltin");
        assert!(calls[0].2.is_empty());
    }

    // ── Middle-builtin routing (#238) ────────────────────────────────

    #[test]
    fn pipe_middle_builtin_receives_upstream_stdin() {
        // `echo hello | middlebuiltin | cat` — middle builtin should
        // receive the echo output as stdin and its captured output
        // should be fed into cat. With our mock returning Some(0) the
        // builtin "consumes" stdin; we verify it was called and saw
        // the upstream bytes.
        let mut results = std::collections::HashMap::new();
        results.insert("middlebuiltin".to_string(), Some(0));
        let (_code, calls) = run_with_pipeline_builtins(
            "echo hello | middlebuiltin | cat",
            &["middlebuiltin"],
            results,
        );
        assert_eq!(calls.len(), 1, "middle builtin should be called once");
        let (name, _args, stdin) = &calls[0];
        assert_eq!(name, "middlebuiltin");
        // echo hello → text_to_array → Array(["hello"]) → "hello"
        assert_eq!(std::str::from_utf8(stdin).unwrap(), "hello");
    }

    #[test]
    fn pipe_middle_builtin_in_rush_pipe_op_chain() {
        // `echo a | middlebuiltin | first 1` — builtin between shell
        // upstream and Rush pipe op downstream.
        let mut results = std::collections::HashMap::new();
        results.insert("middlebuiltin".to_string(), Some(0));
        let (_code, calls) = run_with_pipeline_builtins(
            "echo a | middlebuiltin | first 1",
            &["middlebuiltin"],
            results,
        );
        assert_eq!(calls.len(), 1);
        assert_eq!(calls[0].0, "middlebuiltin");
    }
}
