//! Shell builtins that must run in-process (cd, export, source, alias, etc.)

use rush_core::eval::Evaluator;
use std::cell::RefCell;
use std::collections::HashMap;

// ── Thread-local state ──────────────────────────────────────────────

thread_local! {
    // cd - : previous directory
    static OLDPWD: RefCell<Option<String>> = const { RefCell::new(None) };
    // pushd/popd directory stack
    static DIR_STACK: RefCell<Vec<String>> = const { RefCell::new(Vec::new()) };
    // alias registry: name → expansion
    static ALIASES: RefCell<HashMap<String, String>> = RefCell::new(HashMap::new());
    // job table for background processes
    pub(crate) static JOB_TABLE: RefCell<rush_core::jobs::JobTable> = RefCell::new(rush_core::jobs::JobTable::new());
}

/// Try to handle a line as a shell builtin. Returns true if handled.
pub fn handle(evaluator: &mut Evaluator, line: &str) -> bool {
    // Expand aliases first
    let expanded = expand_alias(line);
    let line = expanded.as_deref().unwrap_or(line);

    let parts: Vec<&str> = line.splitn(2, char::is_whitespace).collect();
    let cmd = parts[0];
    let args = parts.get(1).map(|s| s.trim()).unwrap_or("");

    match cmd {
        "cd" => { handle_cd(evaluator, args); true }
        "export" => { handle_export(args); true }
        "unset" => { handle_unset(args); true }
        "source" | "." => { handle_source(evaluator, args); true }
        "clear" => { print!("\x1b[2J\x1b[H"); true }
        "exit" | "quit" => std::process::exit(evaluator.exit_code),
        "pwd" => {
            let cwd = std::env::current_dir().unwrap_or_default()
                .to_string_lossy().replace('\\', "/");
            println!("{cwd}");
            true
        }
        "alias" => { handle_alias(args); true }
        "unalias" => { handle_unalias(args); true }
        "pushd" => { handle_pushd(evaluator, args); true }
        "popd" => { handle_popd(evaluator); true }
        "dirs" => { handle_dirs(); true }
        "history" => { handle_history(args); true }
        "path" => { handle_path(args); true }
        "help" => { handle_help(evaluator, args); true }
        "set" => { handle_set(args); true }
        "setbg" => { handle_setbg(args); true }
        ".." => { handle_cd(evaluator, ".."); true }
        "..." => { handle_cd(evaluator, "../.."); true }
        "...." => { handle_cd(evaluator, "../../.."); true }
        "which" | "type" => { handle_which(args); true }
        "o" | "open" => { handle_open(args); true }
        "reload" => { handle_reload(evaluator, args); true }
        "sync" => { rush_core::sync::handle_sync(args); true }
        "ai" => { handle_ai(args); true }
        "sql" => { handle_sql(args); true }
        "init" => { handle_init(); true }
        "printf" => { handle_printf(args); true }
        "mark" | "---" => { handle_mark(args); true }
        ":" | "true" => { evaluator.exit_code = 0; true }
        "false" => { evaluator.exit_code = 1; true }
        "command" => { handle_command(evaluator, args); true }
        "read" => { handle_read(evaluator, args); true }
        "exec" => { handle_exec(args); true }
        "eval" => { handle_eval(evaluator, args); true }
        "trap" => { rush_core::trap::handle_trap(args); true }
        "umask" => { handle_umask(args); true }
        "hash" => { true } // no-op: we don't cache command locations
        "readonly" => { handle_readonly(evaluator, args); true }
        "shift" => { handle_shift(evaluator, args); true }
        "getopts" => { handle_getopts(evaluator, args); true }
        "ulimit" => { handle_ulimit(args); true }
        "times" => { handle_times(); true }
        "fc" => { handle_fc(evaluator, args); true }
        "jobs" => { JOB_TABLE.with(|jt| jt.borrow().list()); true }
        "fg" => {
            let code = JOB_TABLE.with(|jt| {
                jt.borrow_mut().foreground(if args.is_empty() { None } else { Some(args) })
            });
            evaluator.exit_code = code.unwrap_or(1);
            true
        }
        "bg" => {
            JOB_TABLE.with(|jt| {
                jt.borrow_mut().background(if args.is_empty() { None } else { Some(args) });
            });
            true
        }
        "kill" if args.contains('%') => {
            handle_kill_job(args);
            true
        }
        "wait" => {
            let code = JOB_TABLE.with(|jt| {
                jt.borrow_mut().wait(if args.is_empty() { None } else { Some(args) })
            });
            evaluator.exit_code = code;
            true
        }
        _ => false,
    }
}

// ── cd ──────────────────────────────────────────────────────────────

fn handle_cd(evaluator: &mut Evaluator, target: &str) {
    let current = cwd_string();

    let path = if target.is_empty() || target == "~" {
        home_dir()
    } else if target == "-" {
        let prev = OLDPWD.with(|p| p.borrow().clone()).unwrap_or_else(home_dir);
        println!("{prev}");
        prev
    } else if let Some(rest) = target.strip_prefix("~/") {
        format!("{}/{rest}", home_dir())
    } else {
        target.to_string()
    };

    match std::env::set_current_dir(&path) {
        Ok(()) => {
            OLDPWD.with(|p| *p.borrow_mut() = Some(current));
            evaluator.exit_code = 0;
        }
        Err(e) => {
            eprintln!("cd: {path}: {e}");
            evaluator.exit_code = 1;
        }
    }
}

// ── export / unset ──────────────────────────────────────────────────

fn handle_export(args: &str) {
    if args.is_empty() {
        let mut vars: Vec<(String, String)> = std::env::vars().collect();
        vars.sort_by(|a, b| a.0.cmp(&b.0));
        for (k, v) in vars {
            println!("export {k}={v}");
        }
        return;
    }
    if let Some((key, value)) = args.split_once('=') {
        let key = key.trim();
        let value = value.trim().trim_matches('"').trim_matches('\'');
        unsafe { std::env::set_var(key, value) };
    } else {
        eprintln!("export: usage: export NAME=VALUE");
    }
}

fn handle_unset(args: &str) {
    for name in args.split_whitespace() {
        unsafe { std::env::remove_var(name) };
    }
}

// ── source ──────────────────────────────────────────────────────────

fn handle_source(evaluator: &mut Evaluator, path: &str) {
    if path.is_empty() {
        eprintln!("source: usage: source <file>");
        return;
    }
    let expanded = expand_tilde(path);
    match std::fs::read_to_string(&expanded) {
        Ok(content) => run_script(evaluator, &content, &expanded),
        Err(e) => eprintln!("source: {expanded}: {e}"),
    }
}

// ── alias / unalias ─────────────────────────────────────────────────

fn handle_alias(args: &str) {
    if args.is_empty() {
        // Show all aliases
        ALIASES.with(|a| {
            let aliases = a.borrow();
            let mut sorted: Vec<_> = aliases.iter().collect();
            sorted.sort_by_key(|(k, _)| (*k).clone());
            for (name, expansion) in sorted {
                println!("alias {name}='{expansion}'");
            }
        });
        return;
    }

    // alias name='expansion' or alias name=expansion
    if let Some((name, expansion)) = args.split_once('=') {
        let name = name.trim().to_string();
        let expansion = expansion.trim().trim_matches('\'').trim_matches('"').to_string();

        // --save: persist to config
        let save = expansion.contains("--save") || args.contains("--save");
        let expansion = expansion.replace("--save", "").trim().to_string();

        ALIASES.with(|a| {
            a.borrow_mut().insert(name.clone(), expansion.clone());
        });

        if save {
            save_alias_to_config(&name, &expansion);
        }
    } else {
        // alias name — show specific alias
        let name = args.trim();
        ALIASES.with(|a| {
            if let Some(exp) = a.borrow().get(name) {
                println!("alias {name}='{exp}'");
            } else {
                eprintln!("alias: {name}: not found");
            }
        });
    }
}

fn handle_unalias(args: &str) {
    for name in args.split_whitespace() {
        ALIASES.with(|a| {
            a.borrow_mut().remove(name);
        });
    }
}

fn expand_alias(line: &str) -> Option<String> {
    let first_word = line.split_whitespace().next()?;
    ALIASES.with(|a| {
        let aliases = a.borrow();
        aliases.get(first_word).map(|expansion| {
            let rest = line[first_word.len()..].to_string();
            format!("{expansion}{rest}")
        })
    })
}

fn save_alias_to_config(name: &str, expansion: &str) {
    let config_path = config_dir().join("config.json");
    let mut config: serde_json::Value = if config_path.exists() {
        std::fs::read_to_string(&config_path)
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or(serde_json::json!({}))
    } else {
        serde_json::json!({})
    };

    let aliases = config.as_object_mut().unwrap()
        .entry("aliases")
        .or_insert(serde_json::json!({}));
    aliases[name] = serde_json::Value::String(expansion.to_string());

    if let Ok(json) = serde_json::to_string_pretty(&config) {
        std::fs::write(&config_path, json).ok();
    }
}

// ── pushd / popd / dirs ─────────────────────────────────────────────

fn handle_pushd(evaluator: &mut Evaluator, target: &str) {
    let current = cwd_string();

    let path = if target.is_empty() {
        // pushd with no args: swap top two
        let top = DIR_STACK.with(|s| s.borrow_mut().pop());
        match top {
            Some(dir) => {
                DIR_STACK.with(|s| s.borrow_mut().push(current.clone()));
                dir
            }
            None => {
                eprintln!("pushd: no other directory");
                return;
            }
        }
    } else {
        DIR_STACK.with(|s| s.borrow_mut().push(current));
        expand_tilde(target)
    };

    match std::env::set_current_dir(&path) {
        Ok(()) => {
            evaluator.exit_code = 0;
            handle_dirs();
        }
        Err(e) => {
            eprintln!("pushd: {path}: {e}");
            // Restore stack
            DIR_STACK.with(|s| { s.borrow_mut().pop(); });
            evaluator.exit_code = 1;
        }
    }
}

fn handle_popd(evaluator: &mut Evaluator) {
    let dir = DIR_STACK.with(|s| s.borrow_mut().pop());
    match dir {
        Some(path) => {
            match std::env::set_current_dir(&path) {
                Ok(()) => {
                    evaluator.exit_code = 0;
                    handle_dirs();
                }
                Err(e) => {
                    eprintln!("popd: {path}: {e}");
                    evaluator.exit_code = 1;
                }
            }
        }
        None => {
            eprintln!("popd: directory stack empty");
            evaluator.exit_code = 1;
        }
    }
}

fn handle_dirs() {
    let cwd = cwd_string();
    let stack = DIR_STACK.with(|s| s.borrow().clone());
    print!("{cwd}");
    for dir in stack.iter().rev() {
        print!(" {dir}");
    }
    println!();
}

// ── history ─────────────────────────────────────────────────────────

fn handle_history(args: &str) {
    if args == "-c" || args == "--clear" {
        let path = config_dir().join("history");
        std::fs::write(&path, "").ok();
        println!("History cleared.");
        return;
    }

    let path = config_dir().join("history");
    if let Ok(content) = std::fs::read_to_string(&path) {
        let lines: Vec<&str> = content.lines().collect();
        let n: usize = args.parse().unwrap_or(lines.len());
        let start = lines.len().saturating_sub(n);
        for (i, line) in lines[start..].iter().enumerate() {
            println!("{:5}  {line}", start + i + 1);
        }
    }
}

// ── path ────────────────────────────────────────────────────────────

fn handle_path(args: &str) {
    let parts: Vec<&str> = args.split_whitespace().collect();
    let subcmd = parts.first().copied().unwrap_or("");

    match subcmd {
        "" | "show" => {
            // path — show current PATH
            let path = std::env::var("PATH").unwrap_or_default();
            let sep = if cfg!(windows) { ';' } else { ':' };
            let mut seen = std::collections::HashSet::new();
            for dir in path.split(sep) {
                let exists = std::path::Path::new(dir).is_dir();
                let dup = !seen.insert(dir.to_string());
                let mark = if !exists {
                    "✗"
                } else if dup {
                    "↑"
                } else {
                    "✓"
                };
                println!("  {mark} {dir}");
            }
        }
        "add" => {
            let dirs: Vec<&str> = parts[1..].iter()
                .filter(|d| **d != "--save")
                .copied()
                .collect();
            let save = parts.contains(&"--save");

            if dirs.is_empty() {
                eprintln!("path add: missing directory");
                return;
            }

            let current = std::env::var("PATH").unwrap_or_default();
            let sep = if cfg!(windows) { ";" } else { ":" };
            for dir in &dirs {
                let expanded = expand_tilde(dir);
                let new_path = format!("{current}{sep}{expanded}");
                unsafe { std::env::set_var("PATH", &new_path) };
            }

            if save {
                for dir in &dirs {
                    let expanded = expand_tilde(dir);
                    save_path_to_init(&format!("path add {expanded}"));
                }
            }
        }
        "rm" | "remove" => {
            let target = parts.get(1).copied().unwrap_or("");
            if target.is_empty() {
                eprintln!("path rm: missing directory");
                return;
            }
            let expanded = expand_tilde(target);
            let current = std::env::var("PATH").unwrap_or_default();
            let sep = if cfg!(windows) { ';' } else { ':' };
            let new_path: Vec<&str> = current.split(sep)
                .filter(|d| *d != expanded)
                .collect();
            unsafe { std::env::set_var("PATH", new_path.join(&sep.to_string())) };
        }
        "check" => {
            handle_path(""); // same as show
        }
        "dedupe" => {
            let current = std::env::var("PATH").unwrap_or_default();
            let sep = if cfg!(windows) { ';' } else { ':' };
            let mut seen = std::collections::HashSet::new();
            let deduped: Vec<&str> = current.split(sep)
                .filter(|d| seen.insert(d.to_string()))
                .collect();
            let removed = current.split(sep).count() - deduped.len();
            unsafe { std::env::set_var("PATH", deduped.join(&sep.to_string())) };
            println!("Removed {removed} duplicate(s).");
        }
        _ => eprintln!("path: unknown subcommand '{subcmd}'. Try: add, rm, check, dedupe"),
    }
}

/// Save a path command to init.rush under a PATH section header.
fn save_path_to_init(path_line: &str) {
    let init_path = config_dir().join("init.rush");

    if !init_path.exists() {
        std::fs::create_dir_all(init_path.parent().unwrap()).ok();
        let content = format!("# ── PATH ─────────────────────────────────────────────────\n{path_line}\n");
        std::fs::write(&init_path, content).ok();
        eprintln!("  saved to:  ~/.config/rush/init.rush");
        return;
    }

    let content = std::fs::read_to_string(&init_path).unwrap_or_default();
    let mut lines: Vec<String> = content.lines().map(|l| l.to_string()).collect();

    // Find PATH section header
    let path_section_idx = lines.iter().position(|l| l.trim_start().starts_with("# ── PATH"));

    if let Some(idx) = path_section_idx {
        // Insert after the last path add line in this section
        let mut insert_at = idx + 1;
        while insert_at < lines.len() {
            let trimmed = lines[insert_at].trim_start();
            if trimmed.starts_with("path add") || trimmed.starts_with("# path add") || trimmed.is_empty() && insert_at == idx + 1 {
                insert_at += 1;
            } else {
                break;
            }
        }
        lines.insert(insert_at, path_line.to_string());
    } else {
        // No PATH section — append one
        if !lines.last().map(|l| l.is_empty()).unwrap_or(true) {
            lines.push(String::new());
        }
        lines.push("# ── PATH ─────────────────────────────────────────────────".to_string());
        lines.push(path_line.to_string());
    }

    let new_content = lines.join("\n") + "\n";
    std::fs::write(&init_path, new_content).ok();
    eprintln!("  saved to:  ~/.config/rush/init.rush");
}

// ── help ────────────────────────────────────────────────────────────

fn handle_help(_evaluator: &mut Evaluator, topic: &str) {
    if topic.is_empty() {
        println!("Rush — a Unix-style shell\n");
        println!("Navigation:");
        println!("  cd [dir]         Change directory (cd -, cd ~, .., ..., ....)");
        println!("  pushd/popd/dirs  Directory stack");
        println!("  o [path]         Open file/dir/URL (macOS: open)");
        println!();
        println!("Environment:");
        println!("  export K=V       Set env var (--save to persist to init.rush)");
        println!("  unset NAME       Remove env var");
        println!("  alias k='v'      Define alias (--save to persist)");
        println!("  path [cmd]       PATH management (add, rm, check, dedupe)");
        println!();
        println!("Configuration:");
        println!("  set [option]     Show/change settings (vi, emacs, -e, -x)");
        println!("  set --secret K V Save API key to secrets.rush");
        println!("  setbg [hex]      Set terminal background color");
        println!("  init             Edit init.rush in $EDITOR");
        println!("  reload [--hard]  Reload config / restart shell");
        println!("  sync [cmd]       Config sync (init, push, pull, status)");
        println!();
        println!("I/O & Scripting:");
        println!("  puts/print/warn  Output text");
        println!("  printf fmt args  Formatted output");
        println!("  read [-p] var    Read line from stdin");
        println!("  source file      Run script in current session");
        println!("  eval args        Execute string as command");
        println!("  exec cmd         Replace shell with command");
        println!("  trap cmd signal  Set signal handler");
        println!();
        println!("Jobs:");
        println!("  jobs             List background jobs");
        println!("  fg/bg [%N]       Foreground/background job");
        println!("  wait [%N]        Wait for job");
        println!("  kill [-sig] %N   Kill job");
        println!();
        println!("AI:");
        println!("  ai \"question\"    Ask AI (set --secret ANTHROPIC_API_KEY first)");
        println!();
        println!("Other:");
        println!("  history [n]      Show history (-c to clear)");
        println!("  help [topic]     Show help");
        println!("  mark / ---       Visual separator");
        println!("  which/type cmd   Find command");
        println!("  clear            Clear screen");
        println!("  exit             Exit shell");
        println!();
        println!("Help topics: variables, strings, arrays, hashes, control-flow,");
        println!("             functions, classes, file, dir, time, path, ssh,");
        println!("             parallel, orchestrate, pipes, objectify, ai");
        return;
    }

    match topic.to_lowercase().as_str() {
        "variables" | "vars" => {
            println!("Variables:");
            println!("  x = 42                    Assignment");
            println!("  a, b = 1, 2               Multiple assignment");
            println!("  x += 1                    Compound (+=, -=, *=, /=)");
            println!("  $os, $arch, $hostname      Built-in variables");
            println!("  $?                         Last exit code");
            println!("  env.HOME                   Environment variables");
        }
        "strings" | "string" => {
            println!("Strings:");
            println!("  \"hello #{{name}}\"            Interpolation");
            println!("  'raw string'               No interpolation");
            println!("  .upcase .downcase           Case conversion");
            println!("  .strip .split .replace     Manipulation");
            println!("  .length .empty? .include?  Queries");
            println!("  .start_with? .end_with?    Prefix/suffix check");
            println!("  .chars .lines .to_i .to_f  Conversion");
        }
        "arrays" | "array" => {
            println!("Arrays:");
            println!("  [1, 2, 3]                  Literal");
            println!("  arr[0]  arr[-1]            Index (negative from end)");
            println!("  .length .empty? .first .last");
            println!("  .push .sort .reverse .uniq .flatten");
            println!("  .map {{|x| ...}} .select {{|x| ...}}");
            println!("  .reject .each .reduce .join");
            println!("  .sum .min .max .include?");
        }
        "hashes" | "hash" => {
            println!("Hashes:");
            println!("  {{a: 1, b: 2}}               Literal");
            println!("  h.keys  h.values            Access");
            println!("  h.length  h.empty?          Queries");
        }
        "control-flow" | "control" | "flow" | "if" | "loops" => {
            println!("Control Flow:");
            println!("  if cond ... elsif ... else ... end");
            println!("  unless cond ... end");
            println!("  while cond ... end");
            println!("  until cond ... end");
            println!("  for x in collection ... end");
            println!("  loop ... end");
            println!("  case expr / when val ... / else ... / end");
            println!("  break, next, return");
            println!("  puts \"x\" if condition       Postfix if/unless");
        }
        "functions" | "function" | "def" => {
            println!("Functions:");
            println!("  def name(arg1, arg2 = default)");
            println!("    body");
            println!("  end");
            println!("  name(args)                  Call with parens");
            println!("  puts \"hello\"                Builtins without parens");
        }
        "classes" | "class" => {
            println!("Classes:");
            println!("  class Name < Parent");
            println!("    attr field1, field2");
            println!("    def initialize(args) ... end");
            println!("    def method(args) ... end");
            println!("  end");
        }
        "file" => {
            println!("File stdlib:");
            println!("  File.read(path)             Read file contents");
            println!("  File.write(path, content)   Write file");
            println!("  File.append(path, content)  Append to file");
            println!("  File.exist?(path)           Check existence");
            println!("  File.delete(path)           Delete file");
            println!("  File.size(path)             File size in bytes");
            println!("  File.copy(src, dst)         Copy file");
            println!("  File.move(src, dst)         Move/rename file");
            println!("  File.basename(path)         File name");
            println!("  File.dirname(path)          Directory name");
            println!("  File.ext(path)              Extension");
        }
        "dir" => {
            println!("Dir stdlib:");
            println!("  Dir.list(path)              List directory");
            println!("  Dir.list(path, :files)      Files only");
            println!("  Dir.list(path, :dirs)       Directories only");
            println!("  Dir.exist?(path)            Check existence");
            println!("  Dir.mkdir(path)             Create directory");
            println!("  Dir.rmdir(path)             Remove directory");
            println!("  Dir.pwd                     Current directory");
            println!("  Dir.home                    Home directory");
            println!("  Dir.glob(pattern)           Glob matching");
        }
        "time" => {
            println!("Time stdlib:");
            println!("  Time.now                    Current local time");
            println!("  Time.utc_now                Current UTC time");
            println!("  Time.today                  Today's date");
            println!("  Time.epoch                  Unix timestamp");
            println!();
            println!("Duration literals:");
            println!("  2.hours    → 7200           Seconds");
            println!("  30.minutes → 1800");
            println!("  1.day      → 86400");
        }
        "path" => {
            println!("Path stdlib — cross-platform path handling:");
            println!("  Path.sep                     \"/\" or \"\\\" (platform native)");
            println!("  Path.join(\"a\", \"b\")          Join with /");
            println!("  Path.normalize(p)            Convert \\ to /");
            println!("  Path.native(p)               Convert / to \\ on Windows");
            println!("  Path.expand(\"~/src\")          Expand ~ and normalize");
            println!("  Path.exist?(p)               Check existence");
            println!("  Path.absolute?(p)            Check if absolute");
            println!("  Path.basename(p)             File name");
            println!("  Path.dirname(p)              Parent directory");
            println!("  Path.ext(p)                  Extension (.txt)");
            println!();
            println!("String methods:");
            println!("  \"path\".native_path            / → \\ on Windows");
            println!("  \"path\".unix_path              \\ → /");
            println!();
            println!("Variable: $sep — platform path separator");
        }
        "ssh" => {
            println!("Ssh stdlib — remote command execution:");
            println!("  Ssh.run(host, cmd)           Execute on remote host → hash");
            println!("  Ssh.test(host)               Test connectivity → bool");
            println!();
            println!("Ssh.run returns:");
            println!("  result[\"status\"]              \"success\" or \"error\"");
            println!("  result[\"exit_code\"]           Exit code (0 = success)");
            println!("  result[\"stdout\"]              Command output");
            println!("  result[\"stderr\"]              Error output");
            println!("  result[\"host\"]                Host name");
            println!();
            println!("Example:");
            println!("  r = Ssh.run(\"web1\", \"uptime\")");
            println!("  puts r[\"stdout\"]");
        }
        "parallel" => {
            println!("Parallel execution — concurrent iteration:");
            println!("  parallel x in items ... end           All at once");
            println!("  parallel(4) x in items ... end        4 workers max");
            println!("  parallel(4, 30) x in items ... end    4 workers, 30s timeout");
            println!("  parallel! x in items ... end          Fail-fast on error");
            println!();
            println!("Example:");
            println!("  parallel host in [\"a\", \"b\", \"c\"]");
            println!("    puts Ssh.run(host, \"uptime\")[\"stdout\"]");
            println!("  end");
        }
        "orchestrate" => {
            println!("Orchestrate — task dependency graph:");
            println!("  orchestrate");
            println!("    task \"name\" do ... end");
            println!("    task \"name\", after: \"dep\" do ... end");
            println!("    task \"name\", after: [\"a\", \"b\"] do ... end");
            println!("  end");
            println!();
            println!("Independent tasks run concurrently.");
            println!("Tasks with after: wait for dependencies.");
            println!("Returns hash mapping task names to results.");
        }
        "pipes" | "pipeline" => {
            println!("Pipeline operators (after |):");
            println!("  where field op value        Filter rows");
            println!("  where /pattern/             Regex filter");
            println!("  select field1, field2       Project fields");
            println!("  sort [field] [--desc]       Sort");
            println!("  count                       Count items");
            println!("  first [n]  last [n]         Take first/last");
            println!("  skip n                      Skip first n");
            println!("  sum  avg  min  max          Aggregation");
            println!("  distinct                    Remove duplicates");
            println!("  as json  as csv             Format output");
            println!("  from json  from csv         Parse input");
            println!("  objectify                   Text table → objects");
            println!("  grep pattern                String filter");
        }
        "objectify" => {
            println!("Objectify — convert text tables to structured objects:");
            println!();
            println!("  ps aux | objectify | where CPU > 50 | select USER, PID, COMMAND");
            println!("  df -h | objectify | where Capacity > 80%");
            println!("  docker ps | objectify | where Status =~ /Up/");
            println!();
            println!("Auto-objectify: known commands objectify automatically when piped.");
            println!("Built-in: ps, df, docker ps, netstat, free, kubectl get, and more.");
            println!();
            println!("Config: ~/.config/rush/objectify.yaml");
            println!("  mycommand:                    # default whitespace split");
            println!("  mycommand:");
            println!("    delim: '\\s{{2,}}'            # custom delimiter regex");
            println!("    fixed: true                  # position-based columns");
            println!("    skip: 1                      # skip N lines after header");
            println!("    cols: [A, B, C]              # explicit column names");
            println!();
            println!("See docs/objectify.yaml for the full reference.");
        }
        "ai" => {
            println!("AI integration:");
            println!("  ai \"question\"                Ask with default provider");
            println!("  ai -p openai \"question\"     Specify provider");
            println!("  ai -m gpt-4 \"question\"      Specify model");
            println!();
            println!("Providers: anthropic, openai, gemini, ollama");
            println!("Set API keys:");
            println!("  set --secret ANTHROPIC_API_KEY \"sk-...\"");
            println!("  set --secret OPENAI_API_KEY \"sk-...\"");
        }
        _ => {
            eprintln!("help: unknown topic '{topic}'");
            eprintln!("Available: variables, strings, arrays, hashes, control-flow,");
            eprintln!("           functions, classes, file, dir, time, pipes, ai");
        }
    }
}

// ── set ─────────────────────────────────────────────────────────────

fn handle_set(args: &str) {
    let mut config = rush_core::config::RushConfig::load();

    if args.is_empty() {
        config.display();
        return;
    }

    // POSIX set flags
    if rush_core::flags::handle_set_flag(args) {
        return;
    }

    // set --secret KEY "value" — save to secrets.rush
    if args.starts_with("--secret ") {
        let rest = args[9..].trim();
        let parts: Vec<&str> = rest.splitn(2, char::is_whitespace).collect();
        if parts.len() == 2 {
            let key = parts[0];
            let value = parts[1].trim().trim_matches('"').trim_matches('\'');
            let secrets_path = config_dir().join("secrets.rush");

            // Set in current env
            unsafe { std::env::set_var(key, value) };

            // Append to secrets.rush
            let line = format!("export {key}=\"{value}\"\n");
            let mut content = std::fs::read_to_string(&secrets_path).unwrap_or_default();

            // Replace if key already exists
            if content.contains(&format!("export {key}=")) {
                let new_content: Vec<&str> = content.lines()
                    .map(|l| if l.starts_with(&format!("export {key}=")) { "" } else { l })
                    .filter(|l| !l.is_empty())
                    .collect();
                content = new_content.join("\n") + "\n";
            }
            content.push_str(&line);
            std::fs::write(&secrets_path, &content).ok();
            println!("  Saved {key} to secrets.rush");
        } else {
            eprintln!("set --secret: usage: set --secret KEY \"value\"");
        }
        return;
    }

    // --save flag: persist to config.json (otherwise session-only for most settings)
    let save = args.contains("--save");
    let args = args.replace("--save", "").trim().to_string();
    let args = args.as_str();

    match args {
        "vi" => { config.set("vi", ""); }
        "emacs" => { config.set("emacs", ""); }
        _ => {
            // set key value
            let parts: Vec<&str> = args.splitn(2, char::is_whitespace).collect();
            let key = parts[0];
            let value = parts.get(1).unwrap_or(&"");
            if !config.set(key, value) {
                eprintln!("set: unknown option '{key}'");
                return;
            }
        }
    }

    // vi/emacs always save (mode is fundamental). Others require --save.
    if save || args == "vi" || args == "emacs" {
        if let Err(e) = config.save() {
            eprintln!("set: failed to save config: {e}");
        }
    }
}

// ── setbg ───────────────────────────────────────────────────────────

fn handle_setbg(args: &str) {
    if args.is_empty() || args == "reset" {
        // Reset to default
        unsafe { std::env::remove_var("RUSH_BG") };
        // Restore terminal default background
        print!("\x1b]111\x07"); // OSC 111: reset bg
        use std::io::Write;
        std::io::stdout().flush().ok();
        println!("Background reset.");
        return;
    }

    // --selector: interactive color picker
    if args.contains("--selector") {
        let save = args.contains("--save");
        let local = args.contains("--local");
        if let Some(hex) = run_color_selector() {
            // Apply the selected color
            let apply_args = if save {
                format!("{hex} --save")
            } else if local {
                format!("{hex} --local")
            } else {
                hex
            };
            handle_setbg(&apply_args);
        }
        return;
    }

    let save = args.contains("--save");
    let local = args.contains("--local");
    let hex = args.split_whitespace()
        .find(|w| w.starts_with('#') || w.chars().all(|c| c.is_ascii_hexdigit()))
        .unwrap_or(args.split_whitespace().next().unwrap_or(""));

    let hex = if hex.starts_with('#') { hex.to_string() } else { format!("#{hex}") };

    match rush_core::theme::set_background(&hex, true) {
        Some(theme) => {
            // Re-set color env vars with new theme
            rush_core::theme::set_native_color_env_vars(&theme);
            println!("Background set to {hex} ({})", if theme.is_dark { "dark" } else { "light" });

            if save {
                let mut config = rush_core::config::RushConfig::load();
                config.set("bg", &hex);
                config.save().ok();
            }
            if local {
                std::fs::write(".rushbg", &hex).ok();
            }
        }
        None => eprintln!("setbg: invalid hex color '{hex}'"),
    }
}

// ── which / type ────────────────────────────────────────────────────

fn handle_which(name: &str) {
    if name.is_empty() {
        eprintln!("which: usage: which <command>");
        return;
    }

    // Check builtins
    let builtins = [
        "cd", "export", "unset", "source", "clear", "exit", "quit", "pwd",
        "alias", "unalias", "pushd", "popd", "dirs", "history", "path",
        "help", "set", "setbg", "which", "type",
    ];
    if builtins.contains(&name) {
        println!("{name}: shell builtin");
        return;
    }

    // Check aliases
    ALIASES.with(|a| {
        if let Some(exp) = a.borrow().get(name) {
            println!("{name}: aliased to '{exp}'");
        }
    });

    // Check PATH
    if let Some(path) = rush_core::process::which(name) {
        println!("{path}");
    } else {
        eprintln!("{name}: not found");
    }
}

// ── command — bypass functions/aliases, run external ────────────────

fn handle_command(evaluator: &mut Evaluator, args: &str) {
    if args.is_empty() {
        eprintln!("command: usage: command [-v|-V] name [args]");
        return;
    }

    // command -v name → like which (POSIX)
    if args.starts_with("-v ") || args.starts_with("-V ") {
        let name = args[3..].trim();
        handle_which(name);
        return;
    }

    // command name args → run as external, skip functions/aliases
    let result = rush_core::process::run_native(args);
    evaluator.exit_code = result.exit_code;
    if !result.stderr.is_empty() {
        eprintln!("{}", result.stderr);
    }
}

// ── read — read line from stdin into variable ───────────────────────

fn handle_read(evaluator: &mut Evaluator, args: &str) {
    let mut prompt = None;
    let mut var_name = "REPLY".to_string();

    let parts: Vec<&str> = args.split_whitespace().collect();
    let mut i = 0;
    while i < parts.len() {
        match parts[i] {
            "-p" if i + 1 < parts.len() => {
                prompt = Some(parts[i + 1]);
                i += 2;
            }
            name => {
                var_name = name.to_string();
                i += 1;
            }
        }
    }

    if let Some(p) = prompt {
        use std::io::Write;
        print!("{p} ");
        std::io::stdout().flush().ok();
    }

    let mut line = String::new();
    match std::io::stdin().read_line(&mut line) {
        Ok(0) => {
            evaluator.exit_code = 1; // EOF
        }
        Ok(_) => {
            let value = line.trim_end_matches('\n').trim_end_matches('\r');
            evaluator.env.set(&var_name, rush_core::value::Value::String(value.to_string()));
            evaluator.exit_code = 0;
        }
        Err(_) => {
            evaluator.exit_code = 1;
        }
    }
}

// ── exec — replace process ──────────────────────────────────────────

fn handle_exec(args: &str) {
    if args.is_empty() {
        return; // exec with no args = apply redirections (not implemented)
    }
    let parts = rush_core::process::parse_command_line(args);
    if parts.is_empty() {
        return;
    }

    // On Unix, exec replaces the process
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        let mut cmd = std::process::Command::new(&parts[0]);
        for arg in &parts[1..] {
            cmd.arg(arg);
        }
        let err = cmd.exec(); // does not return on success
        eprintln!("exec: {}: {err}", parts[0]);
        std::process::exit(127);
    }

    #[cfg(not(unix))]
    {
        eprintln!("exec: not supported on this platform");
    }
}

// ── eval — concatenate and execute ──────────────────────────────────

fn handle_eval(evaluator: &mut Evaluator, args: &str) {
    if args.is_empty() {
        evaluator.exit_code = 0;
        return;
    }
    // Route through the same dispatch as any other command
    crate::run_line(evaluator, args);
}

// ── umask ───────────────────────────────────────────────────────────

fn handle_umask(args: &str) {
    #[cfg(unix)]
    {
        if args.is_empty() {
            // Display current umask
            let current = unsafe { libc::umask(0) };
            unsafe { libc::umask(current); } // restore
            println!("{:04o}", current);
        } else {
            // Set umask
            if let Ok(mask) = u32::from_str_radix(args.trim(), 8) {
                unsafe { libc::umask(mask as libc::mode_t); }
            } else {
                eprintln!("umask: invalid octal number '{args}'");
            }
        }
    }
    #[cfg(not(unix))]
    {
        eprintln!("umask: not supported on this platform");
    }
}

// ── readonly ────────────────────────────────────────────────────────

fn handle_readonly(evaluator: &mut Evaluator, args: &str) {
    if args.is_empty() || args == "-p" {
        // List readonly variables — check each var in env
        // (simplified: just report we don't have a list mechanism yet)
        return;
    }
    // readonly VAR=value or readonly VAR
    if let Some((name, value)) = args.split_once('=') {
        let name = name.trim();
        let value = value.trim().trim_matches('"').trim_matches('\'');
        evaluator.env.set(name, rush_core::value::Value::String(value.to_string()));
        evaluator.env.mark_readonly(name);
    } else {
        // readonly VAR — mark existing var as readonly
        evaluator.env.mark_readonly(args.trim());
    }
}

// ── kill %jobid ─────────────────────────────────────────────────────

fn handle_kill_job(args: &str) {
    let parts: Vec<&str> = args.split_whitespace().collect();
    let mut signal = rush_core::platform::Sig::Term; // default SIGTERM
    let mut job_spec = None;

    for part in &parts {
        if part.starts_with('-') && !part.starts_with('%') {
            // Signal specification: -9, -KILL, -SIGTERM
            match part.to_uppercase().trim_start_matches('-').trim_start_matches("SIG") {
                "9" | "KILL" => signal = rush_core::platform::Sig::Kill,
                "15" | "TERM" => signal = rush_core::platform::Sig::Term,
                "2" | "INT" => signal = rush_core::platform::Sig::Int,
                "1" | "HUP" => signal = rush_core::platform::Sig::Hup,
                "3" | "QUIT" => signal = rush_core::platform::Sig::Quit,
                "18" | "CONT" => signal = rush_core::platform::Sig::Cont,
                _ => {}
            }
        } else if part.starts_with('%') {
            job_spec = Some(*part);
        }
    }

    if let Some(spec) = job_spec {
        JOB_TABLE.with(|jt| {
            let table = jt.borrow_mut();
            if let Some(id) = table.resolve_job_spec_pub(Some(spec)) {
                if let Some(job) = table.get_job(id) {
                    let p = rush_core::platform::current();
                    p.kill_pg(job.pgid, signal);
                    eprintln!("[{id}]  Killed                  {}", job.command);
                } else {
                    eprintln!("kill: {spec}: no such job");
                }
            } else {
                eprintln!("kill: {spec}: no such job");
            }
        });
    }
}

// ── ai — LLM assistant ──────────────────────────────────────────────

fn handle_ai(args: &str) {
    if args.is_empty() {
        eprintln!("usage: ai \"question\" [-p provider] [-m model]");
        return;
    }
    let (prompt, provider, model) = rush_core::ai::parse_ai_args(args);
    if prompt.is_empty() {
        eprintln!("usage: ai \"question\"");
        return;
    }
    match rush_core::ai::execute(provider.as_deref(), model.as_deref(), &prompt, None) {
        Ok(_response) => {
            // Response already printed via streaming
        }
        Err(e) => eprintln!("ai: {e}"),
    }
}

// ── sql — native SQL command ────────────────────────────────────────

fn handle_sql(args: &str) {
    if args.is_empty() {
        eprintln!("usage: sql <connection> <query>");
        eprintln!("  sql :memory: \"SELECT 1+1\"              SQLite in-memory");
        eprintln!("  sql path/to/db.sqlite \"SELECT ...\"     SQLite file");
        eprintln!("  sql postgres://host/db \"SELECT ...\"    PostgreSQL");
        return;
    }

    let parsed = rush_core::process::parse_command_line(args);
    if parsed.len() < 2 {
        eprintln!("sql: need <connection> and <query>");
        return;
    }

    let conn = &parsed[0];
    let query = &parsed[1];

    // SQLite (file or :memory:)
    if conn == ":memory:" || conn.ends_with(".sqlite") || conn.ends_with(".sqlite3") || conn.ends_with(".db") {
        let result = std::process::Command::new("sqlite3")
            .args(["-header", "-column", conn, query])
            .status();
        match result {
            Ok(status) if !status.success() => {
                eprintln!("sql: sqlite3 exited with {}", status.code().unwrap_or(-1));
            }
            Err(_) => eprintln!("sql: sqlite3 not found (install: brew install sqlite3)"),
            _ => {}
        }
        return;
    }

    // PostgreSQL
    if conn.starts_with("postgres://") || conn.starts_with("postgresql://") {
        let result = std::process::Command::new("psql")
            .args([conn.as_str(), "-c", query])
            .status();
        match result {
            Ok(status) if !status.success() => {
                eprintln!("sql: psql exited with {}", status.code().unwrap_or(-1));
            }
            Err(_) => eprintln!("sql: psql not found (install: brew install postgresql)"),
            _ => {}
        }
        return;
    }

    eprintln!("sql: unsupported connection string '{conn}'");
    eprintln!("     Supported: :memory:, *.sqlite, *.db, postgres://...");
}

// ── printf — formatted output ────────────────────────────────────────

fn handle_printf(args: &str) {
    if args.is_empty() {
        return;
    }
    // Parse with shell quoting awareness
    let parsed = rush_core::process::parse_command_line(args);
    if parsed.is_empty() {
        return;
    }
    let fmt = &parsed[0];
    let values: Vec<&str> = parsed[1..].iter().map(|s| s.as_str()).collect();

    let mut val_idx = 0;
    let mut output = String::new();
    let chars: Vec<char> = fmt.chars().collect();
    let mut i = 0;

    while i < chars.len() {
        if chars[i] == '\\' && i + 1 < chars.len() {
            match chars[i + 1] {
                'n' => output.push('\n'),
                't' => output.push('\t'),
                'r' => output.push('\r'),
                '\\' => output.push('\\'),
                _ => { output.push('\\'); output.push(chars[i + 1]); }
            }
            i += 2;
        } else if chars[i] == '%' && i + 1 < chars.len() {
            let val = values.get(val_idx).unwrap_or(&"");
            val_idx += 1;
            match chars[i + 1] {
                's' => output.push_str(val),
                'd' => output.push_str(&val.parse::<i64>().unwrap_or(0).to_string()),
                'f' => output.push_str(&format!("{:.6}", val.parse::<f64>().unwrap_or(0.0))),
                '%' => { output.push('%'); val_idx -= 1; }
                _ => { output.push('%'); output.push(chars[i + 1]); val_idx -= 1; }
            }
            i += 2;
        } else {
            output.push(chars[i]);
            i += 1;
        }
    }

    print!("{output}");
    use std::io::Write;
    std::io::stdout().flush().ok();
}

// ── mark — visual separator ─────────────────────────────────────────

fn handle_mark(args: &str) {
    let width = std::env::var("COLUMNS").and_then(|s| s.parse().map_err(|_| std::env::VarError::NotPresent)).unwrap_or(80);
    if args.is_empty() || args == "---" {
        println!("{}", "─".repeat(width));
    } else {
        let label = args.trim_matches('"').trim_matches('\'');
        let pad = width.saturating_sub(label.len() + 4);
        println!("── {} {}", label, "─".repeat(pad));
    }
}

// ── init — open init.rush in $EDITOR ─────────────────────────────────

fn handle_init() {
    let editor = std::env::var("EDITOR").unwrap_or_else(|_| {
        if cfg!(windows) { "notepad".into() } else { "vi".into() }
    });
    let init_path = config_dir().join("init.rush");

    // Create if doesn't exist
    if !init_path.exists() {
        std::fs::create_dir_all(init_path.parent().unwrap()).ok();
        std::fs::write(&init_path, "# ~/.config/rush/init.rush\n# Startup script — runs on every shell launch.\n").ok();
    }

    let path_str = init_path.to_string_lossy().to_string();
    std::process::Command::new(&editor)
        .arg(&path_str)
        .status()
        .ok();

    eprintln!("Tip: run 'reload' to apply changes.");
}

// ── o (open) — cross-platform open ──────────────────────────────────

fn handle_open(args: &str) {
    if args.is_empty() {
        #[cfg(target_os = "macos")]
        { std::process::Command::new("open").arg(".").status().ok(); }
        #[cfg(target_os = "linux")]
        { std::process::Command::new("xdg-open").arg(".").status().ok(); }
        return;
    }

    // Open each argument separately
    for target in args.split_whitespace() {
        let target = if target.starts_with("~/") {
            format!("{}/{}", std::env::var("HOME").unwrap_or_default(), &target[2..])
        } else {
            target.to_string()
        };

        #[cfg(target_os = "macos")]
        { std::process::Command::new("open").arg(&target).status().ok(); }
        #[cfg(target_os = "linux")]
        { std::process::Command::new("xdg-open").arg(&target).status().ok(); }
        #[cfg(target_os = "windows")]
        { std::process::Command::new("cmd").args(["/C", "start", "", &target]).status().ok(); }
    }
}

// ── reload — re-run init.rush or restart shell ──────────────────────

fn handle_reload(evaluator: &mut Evaluator, args: &str) {
    if args == "--hard" {
        // Hard reload: re-exec the rush binary
        let exe = std::env::current_exe().unwrap_or_else(|_| "rush".into());
        eprintln!("Reloading rush...");

        #[cfg(unix)]
        {
            use std::os::unix::process::CommandExt;
            let err = std::process::Command::new(&exe).exec();
            eprintln!("reload --hard: {err}");
        }
        #[cfg(not(unix))]
        {
            eprintln!("reload --hard: not supported on this platform");
        }
        return;
    }

    // Soft reload: re-run init.rush and re-detect theme
    eprintln!("Reloading config...");

    // Re-detect theme
    let _theme = rush_core::theme::initialize();

    // Re-load aliases
    load_aliases_from_config();

    // Re-load secrets
    load_secrets(evaluator);

    // Re-load init.rush
    load_init(evaluator);

    eprintln!("Reloaded.");
}

// ── fc — history editing ─────────────────────────────────────────────

fn handle_fc(evaluator: &mut Evaluator, args: &str) {
    let history_path = config_dir().join("history");
    let content = std::fs::read_to_string(&history_path).unwrap_or_default();
    let lines: Vec<&str> = content.lines().collect();

    if args.is_empty() || args == "-l" {
        // List last 16 history entries
        let start = lines.len().saturating_sub(16);
        for (i, line) in lines[start..].iter().enumerate() {
            println!("{:5}  {line}", start + i + 1);
        }
        return;
    }

    if args.starts_with("-l ") {
        // fc -l first [last]
        let parts: Vec<&str> = args[3..].split_whitespace().collect();
        let first: usize = parts.first().and_then(|s| s.parse().ok()).unwrap_or(lines.len().saturating_sub(16));
        let last: usize = parts.get(1).and_then(|s| s.parse().ok()).unwrap_or(lines.len());
        let first = first.saturating_sub(1).min(lines.len());
        let last = last.min(lines.len());
        for (i, line) in lines[first..last].iter().enumerate() {
            println!("{:5}  {line}", first + i + 1);
        }
        return;
    }

    if args == "-e -" || args.starts_with("-s") {
        // fc -s [pat=rep] [cmd] — re-execute last command
        if let Some(last) = lines.last() {
            println!("{last}");
            crate::run_line(evaluator, last);
        }
        return;
    }

    eprintln!("fc: usage: fc [-l] [first [last]] | fc -s [cmd]");
}

// ── shift — shift positional parameters ──────────────────────────────

fn handle_shift(evaluator: &mut Evaluator, args: &str) {
    let n: usize = args.trim().parse().unwrap_or(1);
    if let Some(rush_core::value::Value::Array(ref argv)) = evaluator.env.get("ARGV").cloned() {
        let new_argv: Vec<rush_core::value::Value> = argv.iter().skip(n).cloned().collect();
        evaluator.env.set("ARGV", rush_core::value::Value::Array(new_argv));
        evaluator.exit_code = 0;
    } else {
        evaluator.exit_code = 1;
    }
}

// ── getopts — option parsing for scripts ────────────────────────────

fn handle_getopts(evaluator: &mut Evaluator, args: &str) {
    // getopts optstring name [arg...]
    // Simplified: parse next option from ARGV
    let parts: Vec<&str> = args.split_whitespace().collect();
    if parts.len() < 2 {
        eprintln!("getopts: usage: getopts optstring name [args...]");
        evaluator.exit_code = 1;
        return;
    }

    let optstring = parts[0];
    let name = parts[1];

    // Get OPTIND (1-based index into args)
    let optind: usize = evaluator.env.get("OPTIND")
        .and_then(|v| v.to_int())
        .unwrap_or(1) as usize;

    // Get args (from ARGV or remaining parts)
    let argv: Vec<String> = if parts.len() > 2 {
        parts[2..].iter().map(|s| s.to_string()).collect()
    } else {
        evaluator.env.get("ARGV")
            .and_then(|v| if let rush_core::value::Value::Array(arr) = v {
                Some(arr.iter().map(|v| v.to_rush_string()).collect())
            } else { None })
            .unwrap_or_default()
    };

    if optind > argv.len() {
        evaluator.exit_code = 1;
        return;
    }

    let arg = &argv[optind - 1];
    if !arg.starts_with('-') || arg == "-" || arg == "--" {
        evaluator.exit_code = 1;
        return;
    }

    let opt = &arg[1..2]; // first option char
    evaluator.env.set(name, rush_core::value::Value::String(opt.to_string()));

    // Check if option takes an argument
    if let Some(pos) = optstring.find(opt) {
        if optstring.get(pos + 1..pos + 2) == Some(":") {
            // Option takes argument
            if arg.len() > 2 {
                evaluator.env.set("OPTARG", rush_core::value::Value::String(arg[2..].to_string()));
            } else if optind < argv.len() {
                evaluator.env.set("OPTARG", rush_core::value::Value::String(argv[optind].clone()));
                evaluator.env.set("OPTIND", rush_core::value::Value::Int(optind as i64 + 2));
                evaluator.exit_code = 0;
                return;
            }
        }
    }

    evaluator.env.set("OPTIND", rush_core::value::Value::Int(optind as i64 + 1));
    evaluator.exit_code = 0;
}

// ── ulimit — resource limits ────────────────────────────────────────

fn handle_ulimit(args: &str) {
    #[cfg(unix)]
    {
        if args.is_empty() || args == "-f" {
            // Default: show file size limit
            let mut rlim: libc::rlimit = unsafe { std::mem::zeroed() };
            unsafe { libc::getrlimit(libc::RLIMIT_FSIZE, &mut rlim); }
            if rlim.rlim_cur == libc::RLIM_INFINITY {
                println!("unlimited");
            } else {
                println!("{}", rlim.rlim_cur / 512); // in 512-byte blocks
            }
        } else if args == "-a" {
            // Show all limits
            let limits = [
                ("core file size", libc::RLIMIT_CORE),
                ("data seg size", libc::RLIMIT_DATA),
                ("file size", libc::RLIMIT_FSIZE),
                ("max memory size", libc::RLIMIT_RSS),
                ("open files", libc::RLIMIT_NOFILE),
                ("stack size", libc::RLIMIT_STACK),
                ("cpu time", libc::RLIMIT_CPU),
            ];
            for (name, resource) in &limits {
                let mut rlim: libc::rlimit = unsafe { std::mem::zeroed() };
                unsafe { libc::getrlimit(*resource, &mut rlim); }
                let val = if rlim.rlim_cur == libc::RLIM_INFINITY {
                    "unlimited".to_string()
                } else {
                    rlim.rlim_cur.to_string()
                };
                println!("{name:20} {val}");
            }
        } else if args == "-n" {
            let mut rlim: libc::rlimit = unsafe { std::mem::zeroed() };
            unsafe { libc::getrlimit(libc::RLIMIT_NOFILE, &mut rlim); }
            println!("{}", rlim.rlim_cur);
        } else {
            eprintln!("ulimit: usage: ulimit [-a|-f|-n] [limit]");
        }
    }
    #[cfg(not(unix))]
    {
        eprintln!("ulimit: not supported on this platform");
    }
}

// ── times — process times ───────────────────────────────────────────

fn handle_times() {
    #[cfg(unix)]
    {
        let mut tms: libc::tms = unsafe { std::mem::zeroed() };
        unsafe { libc::times(&mut tms); }
        let ticks = unsafe { libc::sysconf(libc::_SC_CLK_TCK) } as f64;
        println!("{}m{:.3}s {}m{:.3}s",
            (tms.tms_utime as f64 / ticks / 60.0) as u64,
            (tms.tms_utime as f64 / ticks) % 60.0,
            (tms.tms_stime as f64 / ticks / 60.0) as u64,
            (tms.tms_stime as f64 / ticks) % 60.0,
        );
        println!("{}m{:.3}s {}m{:.3}s",
            (tms.tms_cutime as f64 / ticks / 60.0) as u64,
            (tms.tms_cutime as f64 / ticks) % 60.0,
            (tms.tms_cstime as f64 / ticks / 60.0) as u64,
            (tms.tms_cstime as f64 / ticks) % 60.0,
        );
    }
    #[cfg(not(unix))]
    {
        println!("0m0.000s 0m0.000s");
        println!("0m0.000s 0m0.000s");
    }
}

// ── init.rush loading ───────────────────────────────────────────────

pub fn load_init(evaluator: &mut Evaluator) {
    let init_path = config_dir().join("init.rush");
    if init_path.exists() {
        if let Ok(content) = std::fs::read_to_string(&init_path) {
            run_script(evaluator, &content, "init.rush");
        }
    }
}

/// Run a script handling shell builtins (path, export, alias) line-by-line,
/// and batching Rush syntax for the parser.
/// Run a script with per-line dispatch: builtins, Rush syntax, shell commands.
/// Public so script files and init.rush use the same path.
pub fn run_script(evaluator: &mut Evaluator, content: &str, source_name: &str) {
    let mut rush_buf = String::new();
    let mut block_depth: i32 = 0; // track def/if/for/while...end nesting
    let mut path_block: Option<Vec<String>> = None; // path add...end accumulator

    let block_openers = [
        "if", "unless", "while", "until", "for", "parallel", "parallel!",
        "orchestrate", "task", "loop",
        "def", "class", "enum", "case", "match", "try", "begin",
        "macos", "linux", "win64", "win32", "ps", "ps5", "plugin",
    ];

    for line in content.lines() {
        let trimmed = line.trim();

        // ── path add...end / path rm...end block accumulation ──
        if let Some(ref mut paths) = path_block {
            if trimmed.eq_ignore_ascii_case("end") {
                // Execute accumulated path commands
                let subcmd = if paths.first().map(|s| s.contains("rm")).unwrap_or(false) { "rm" } else { "add" };
                for dir in paths.drain(..) {
                    let dir = dir.trim().to_string();
                    if !dir.is_empty() && !dir.starts_with('#') {
                        handle_path(&format!("{subcmd} {dir}"));
                    }
                }
                path_block = None;
                continue;
            }
            paths.push(trimmed.to_string());
            continue;
        }

        // Detect start of path add...end or path rm...end block
        if (trimmed.starts_with("path add") || trimmed.starts_with("path rm"))
            && !trimmed.contains("--save")
        {
            // Check if this is the start of a block (next lines until "end")
            let rest = if trimmed.starts_with("path add") {
                trimmed[8..].trim()
            } else {
                trimmed[7..].trim()
            };
            // If there are inline arguments, execute normally
            if !rest.is_empty() {
                flush_rush_buf(evaluator, &rush_buf, source_name);
                rush_buf.clear();
                handle(evaluator, trimmed);
                continue;
            }
            // No inline args — this is a block start
            path_block = Some(Vec::new());
            continue;
        }

        // Comments
        if trimmed.starts_with('#') {
            if block_depth > 0 {
                rush_buf.push_str(line);
                rush_buf.push('\n');
            }
            continue;
        }

        // Track block depth from keywords
        let first_word = trimmed.split_whitespace().next().unwrap_or("");
        // Strip parenthesized options: parallel(4, 10) → parallel
        let base_word = first_word.split('(').next().unwrap_or(first_word);
        // Check first word, or after `=` for assignment-wrapped blocks (e.g. `x = for ...`)
        let effective_opener = if block_openers.iter().any(|k| k.eq_ignore_ascii_case(first_word))
            || block_openers.iter().any(|k| k.eq_ignore_ascii_case(base_word)) {
            true
        } else if let Some(after_eq) = trimmed.split_once('=').and_then(|(lhs, rhs)| {
            // Only simple assignments (no ==, !=, <=, >=, +=, etc.)
            if lhs.ends_with('!') || lhs.ends_with('<') || lhs.ends_with('>')
                || lhs.ends_with('+') || lhs.ends_with('-') || lhs.ends_with('*')
                || lhs.ends_with('/') || rhs.starts_with('=') || rhs.starts_with('~') {
                None
            } else {
                Some(rhs.trim())
            }
        }) {
            let rhs_first = after_eq.split_whitespace().next().unwrap_or("");
            block_openers.iter().any(|k| k.eq_ignore_ascii_case(rhs_first))
        } else {
            false
        };
        if effective_opener {
            block_depth += 1;
        }
        if first_word.eq_ignore_ascii_case("end") && block_depth > 0 {
            block_depth -= 1;
        }

        // Inside a block — accumulate everything (even shell commands)
        if block_depth > 0 || (first_word.eq_ignore_ascii_case("end") && rush_buf.contains("def ")) {
            rush_buf.push_str(line);
            rush_buf.push('\n');

            // If we just closed the last block, flush
            if block_depth == 0 && first_word.eq_ignore_ascii_case("end") {
                flush_rush_buf(evaluator, &rush_buf, source_name);
                rush_buf.clear();
            }
            continue;
        }

        // Blank lines → flush Rush buf
        if trimmed.is_empty() {
            flush_rush_buf(evaluator, &rush_buf, source_name);
            rush_buf.clear();
            continue;
        }

        // Shell builtins
        if matches!(first_word, "path" | "export" | "unset" | "alias" | "cd" | "source" | "clear") {
            flush_rush_buf(evaluator, &rush_buf, source_name);
            rush_buf.clear();
            handle(evaluator, trimmed);
        }
        // Rush syntax → accumulate
        else if rush_core::triage::is_rush_syntax(trimmed) {
            rush_buf.push_str(line);
            rush_buf.push('\n');
        }
        // Shell command → dispatch
        else {
            flush_rush_buf(evaluator, &rush_buf, source_name);
            rush_buf.clear();
            crate::run_line(evaluator, trimmed);
        }
    }

    // Flush remaining
    flush_rush_buf(evaluator, &rush_buf, source_name);
}

fn flush_rush_buf(evaluator: &mut Evaluator, buf: &str, source_name: &str) {
    let trimmed = buf.trim();
    if trimmed.is_empty() {
        return;
    }
    match rush_core::parser::parse(trimmed) {
        Ok(nodes) => {
            if let Err(e) = evaluator.exec_toplevel(&nodes) {
                eprintln!("{source_name}: {e}");
            }
        }
        Err(e) => {
            // Suppress parse errors for function bodies containing shell commands
            // (known limitation: def...end with mkdir/cd/etc inside)
            // Only show errors that aren't from this pattern
            let msg = format!("{e}");
            if !msg.contains("Expected End, got Eof") && !msg.contains("Unexpected token End") {
                eprintln!("{source_name}: {e}");
            }
        }
    }
}

pub fn load_secrets(_evaluator: &mut Evaluator) {
    let secrets_path = config_dir().join("secrets.rush");
    if secrets_path.exists() {
        if let Ok(content) = std::fs::read_to_string(&secrets_path) {
            for line in content.lines() {
                let line = line.trim();
                if line.is_empty() || line.starts_with('#') {
                    continue;
                }
                let export_line = line.strip_prefix("export ").unwrap_or(line);
                if let Some((key, value)) = export_line.split_once('=') {
                    let key = key.trim();
                    let value = value.trim().trim_matches('"').trim_matches('\'');
                    unsafe { std::env::set_var(key, value) };
                }
            }
        }
    }
}

/// Load aliases from config.json
pub fn load_aliases_from_config() {
    let config_path = config_dir().join("config.json");
    if let Ok(content) = std::fs::read_to_string(&config_path) {
        if let Ok(config) = serde_json::from_str::<serde_json::Value>(&content) {
            if let Some(aliases) = config.get("aliases").and_then(|a| a.as_object()) {
                ALIASES.with(|a| {
                    let mut map = a.borrow_mut();
                    for (name, expansion) in aliases {
                        if let Some(exp) = expansion.as_str() {
                            map.insert(name.clone(), exp.to_string());
                        }
                    }
                });
            }
        }
    }
}

// ── Inject built-in variables ───────────────────────────────────────

/// Inject environment variables that .NET's InjectRushEnvVars set.
/// These are available to child processes and Rush scripts.
pub fn inject_env_vars(is_login: bool) {
    let os_name = if cfg!(target_os = "macos") { "macos" }
        else if cfg!(target_os = "linux") { "linux" }
        else { "windows" };
    let arch_name = std::env::consts::ARCH;

    unsafe {
        std::env::set_var("RUSH_OS", os_name);
        std::env::set_var("RUSH_ARCH", arch_name);
        std::env::set_var("RUSH_VERSION", crate::rush_version_short());
        std::env::set_var("RUSH_LOGIN", if is_login { "1" } else { "0" });
    }

    // OS version
    #[cfg(target_os = "macos")]
    {
        if let Ok(output) = std::process::Command::new("sw_vers")
            .args(["-productVersion"])
            .output()
        {
            let ver = String::from_utf8_lossy(&output.stdout).trim().to_string();
            if !ver.is_empty() {
                unsafe { std::env::set_var("RUSH_OS_VERSION", &ver) };
            }
        }
    }
    #[cfg(target_os = "linux")]
    {
        if let Ok(ver) = std::fs::read_to_string("/proc/version") {
            let short = ver.split_whitespace().nth(2).unwrap_or("unknown");
            unsafe { std::env::set_var("RUSH_OS_VERSION", short) };
        }
    }
}

/// Inject built-in Rush variables into the evaluator environment.
pub fn inject_builtin_vars(evaluator: &mut Evaluator) {
    use rush_core::value::Value;
    let os_name = if cfg!(target_os = "macos") { "macos" }
        else if cfg!(target_os = "linux") { "linux" }
        else { "windows" };
    evaluator.env.set("$os", Value::String(os_name.to_string()));
    evaluator.env.set("$arch", Value::String(std::env::consts::ARCH.to_string()));
    evaluator.env.set("$hostname", Value::String(rush_core::llm::get_hostname()));
    evaluator.env.set("$user", Value::String(rush_core::llm::get_username()));
    evaluator.env.set("$rush_version", Value::String(crate::rush_version_short().to_string()));
    evaluator.env.set("$pid", Value::Int(std::process::id() as i64));
    evaluator.env.set("$sep", Value::String(std::path::MAIN_SEPARATOR.to_string()));
    evaluator.env.set("__rush_arch", Value::String(std::env::consts::ARCH.to_string()));
    evaluator.env.set("__rush_os_version", Value::String(
        std::env::var("RUSH_OS_VERSION").unwrap_or_else(|_| "unknown".to_string())
    ));
}

// ── Helpers ─────────────────────────────────────────────────────────

fn home_dir() -> String {
    std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_else(|_| ".".to_string())
}

fn config_dir() -> std::path::PathBuf {
    std::path::PathBuf::from(home_dir()).join(".config").join("rush")
}

// ── Windows coreutils detection ────────────────────────────────────

/// Check for Unix coreutils on Windows. Returns true if found.
/// Shows a one-time hint if not found (tracked via config).
#[cfg(windows)]
pub fn check_windows_coreutils(theme: &rush_core::theme::Theme) -> bool {
    // Check if we already have Unix tools (from Git for Windows, uutils, MSYS2, etc.)
    if rush_core::process::command_exists("ls") {
        return true;
    }

    // Check if we already showed this hint
    let hint_file = config_dir().join(".coreutils-hint-shown");
    if hint_file.exists() {
        return false;
    }

    // Show hint
    eprintln!();
    eprintln!("{}Tip: Install Unix coreutils for the best Rush experience on Windows:{}", theme.muted, theme.reset);
    eprintln!("{}  cargo install coreutils       Rust-native (uutils){}", theme.muted, theme.reset);
    eprintln!("{}  — or use Git for Windows which includes ls, cat, grep, etc.{}", theme.muted, theme.reset);
    eprintln!();

    // Mark hint as shown
    std::fs::write(&hint_file, "").ok();
    false
}

// ── Color selector (setbg --selector) ──────────────────────────────

struct PaletteEntry {
    hex: &'static str,
    name: &'static str,
}

const DARK_PALETTE: &[PaletteEntry] = &[
    PaletteEntry { hex: "#1A1A2E", name: "Midnight Blue" },
    PaletteEntry { hex: "#16213E", name: "Navy" },
    PaletteEntry { hex: "#0F3460", name: "Deep Blue" },
    PaletteEntry { hex: "#002B36", name: "Solarized Dark" },
    PaletteEntry { hex: "#1B4332", name: "Forest Green" },
    PaletteEntry { hex: "#2D6A4F", name: "Emerald" },
    PaletteEntry { hex: "#3A2E1F", name: "Espresso" },
    PaletteEntry { hex: "#4A2020", name: "Dark Maroon" },
    PaletteEntry { hex: "#6B2020", name: "Crimson" },
    PaletteEntry { hex: "#2E1A47", name: "Deep Purple" },
    PaletteEntry { hex: "#4A1942", name: "Plum" },
    PaletteEntry { hex: "#3D1F4E", name: "Grape" },
    PaletteEntry { hex: "#2E3440", name: "Nord" },
    PaletteEntry { hex: "#282A36", name: "Dracula" },
    PaletteEntry { hex: "#282828", name: "Gruvbox Dark" },
    PaletteEntry { hex: "#1E1E2E", name: "Catppuccin" },
    PaletteEntry { hex: "#282C34", name: "One Dark" },
    PaletteEntry { hex: "#1A1B26", name: "Tokyo Night" },
    PaletteEntry { hex: "#263238", name: "Material Ocean" },
    PaletteEntry { hex: "#2D353B", name: "Everforest" },
    PaletteEntry { hex: "#1F3044", name: "Steel Blue" },
    PaletteEntry { hex: "#2A3F54", name: "Petrol" },
    PaletteEntry { hex: "#3B4F2A", name: "Olive" },
    PaletteEntry { hex: "#4E3B31", name: "Chocolate" },
];

const LIGHT_PALETTE: &[PaletteEntry] = &[
    PaletteEntry { hex: "#FDF6E3", name: "Solarized Light" },
    PaletteEntry { hex: "#FBF1C7", name: "Gruvbox Cream" },
    PaletteEntry { hex: "#FFE8E8", name: "Rose Blush" },
    PaletteEntry { hex: "#E8F0FF", name: "Ice Blue" },
    PaletteEntry { hex: "#E8FFE8", name: "Mint" },
    PaletteEntry { hex: "#FFF0E0", name: "Peach" },
    PaletteEntry { hex: "#F0E0FF", name: "Lavender" },
    PaletteEntry { hex: "#E0F0F0", name: "Seafoam" },
    PaletteEntry { hex: "#FFF8E0", name: "Butter" },
    PaletteEntry { hex: "#FFE0F0", name: "Pink" },
    PaletteEntry { hex: "#E0FFE8", name: "Spring" },
    PaletteEntry { hex: "#F5F5F5", name: "Paper" },
];

const GRAYSCALE_PALETTE: &[PaletteEntry] = &[
    PaletteEntry { hex: "#0A0A0A", name: "Near Black" },
    PaletteEntry { hex: "#1A1A1A", name: "Charcoal" },
    PaletteEntry { hex: "#2A2A2A", name: "Dark Gray" },
    PaletteEntry { hex: "#3A3A3A", name: "Gray 23%" },
    PaletteEntry { hex: "#4A4A4A", name: "Gray 29%" },
    PaletteEntry { hex: "#808080", name: "Mid Gray" },
    PaletteEntry { hex: "#B8B8B8", name: "Gray 72%" },
    PaletteEntry { hex: "#D0D0D0", name: "Light Gray" },
    PaletteEntry { hex: "#F0F0F0", name: "Near White" },
];

/// Run the interactive color selector. Returns the selected hex color or None.
fn run_color_selector() -> Option<String> {
    use std::io::{Write, BufRead};

    let sections: &[(&str, &[PaletteEntry])] = &[
        ("Dark Themes", DARK_PALETTE),
        ("Light Themes", LIGHT_PALETTE),
        ("Grayscale", GRAYSCALE_PALETTE),
    ];

    // Display the palette with numbered entries
    println!();
    println!("  Background Color Selector");
    println!("  ─────────────────────────────────────────────────");
    println!();

    let mut index = 1;
    for (label, colors) in sections {
        println!("  {label}:");
        for row in colors.chunks(6) {
            print!("  ");
            for entry in row {
                // Show color swatch using bg color + two spaces
                let (r, g, b) = parse_hex(entry.hex);
                // Use ANSI 24-bit bg color for the swatch
                print!("  \x1b[48;2;{r};{g};{b}m  \x1b[0m ");
                print!("{index:2}) {:<16}", entry.name);
                index += 1;
            }
            println!();
        }
        println!();
    }

    // Also allow typing a hex value
    println!("  Enter number (1-{}) or hex (#RRGGBB), q to cancel:", index - 1);
    print!("  > ");
    std::io::stdout().flush().ok();

    let mut input = String::new();
    std::io::stdin().lock().read_line(&mut input).ok()?;
    let input = input.trim();

    if input.eq_ignore_ascii_case("q") || input.is_empty() {
        return None;
    }

    // Check if it's a hex color
    if input.starts_with('#') && (input.len() == 4 || input.len() == 7) {
        return Some(input.to_string());
    }

    // Parse as number
    if let Ok(n) = input.parse::<usize>() {
        let mut idx = 1;
        for (_, colors) in sections {
            for entry in *colors {
                if idx == n {
                    return Some(entry.hex.to_string());
                }
                idx += 1;
            }
        }
    }

    eprintln!("  Invalid selection.");
    None
}

fn parse_hex(hex: &str) -> (u8, u8, u8) {
    let hex = hex.trim_start_matches('#');
    if hex.len() == 6 {
        let r = u8::from_str_radix(&hex[0..2], 16).unwrap_or(0);
        let g = u8::from_str_radix(&hex[2..4], 16).unwrap_or(0);
        let b = u8::from_str_radix(&hex[4..6], 16).unwrap_or(0);
        (r, g, b)
    } else if hex.len() == 3 {
        let r = u8::from_str_radix(&hex[0..1], 16).unwrap_or(0) * 17;
        let g = u8::from_str_radix(&hex[1..2], 16).unwrap_or(0) * 17;
        let b = u8::from_str_radix(&hex[2..3], 16).unwrap_or(0) * 17;
        (r, g, b)
    } else {
        (0, 0, 0)
    }
}

fn cwd_string() -> String {
    std::env::current_dir()
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_default()
}

fn expand_tilde(path: &str) -> String {
    if let Some(rest) = path.strip_prefix("~/") {
        format!("{}/{rest}", home_dir())
    } else if path == "~" {
        home_dir()
    } else {
        path.to_string()
    }
}
