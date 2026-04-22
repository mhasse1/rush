//! Binary-level script execution tests.
//!
//! Spawns `rush-cli` as a subprocess the way a user would invoke it:
//!
//!   - `rush -c '<program>'` — inline
//!   - `rush script.rush` — explicit path to a script file
//!   - `./script.rush` with `#!/usr/bin/env rush` shebang
//!   - `script.rush` via PATH lookup
//!
//! The in-crate `script_surface_tests` cover language semantics via the
//! in-process dispatch path. This file covers things that only break at
//! the binary boundary: shebang handling, PATH discovery, exit codes,
//! argv propagation.

#![cfg(unix)]

use std::io::Write;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

const RUSH: &str = env!("CARGO_BIN_EXE_rush-cli");

/// Make a throwaway dir for a test so we can drop scripts without
/// polluting $TMPDIR across runs.
fn scratch_dir(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "rush_cli_{label}_{}_{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.subsec_nanos())
            .unwrap_or(0)
    ));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

/// Drop a script file with the given shebang/body and make it executable.
fn write_script(dir: &Path, name: &str, body: &str) -> PathBuf {
    let path = dir.join(name);
    let mut f = std::fs::File::create(&path).unwrap();
    f.write_all(body.as_bytes()).unwrap();
    let mut perm = std::fs::metadata(&path).unwrap().permissions();
    perm.set_mode(0o755);
    std::fs::set_permissions(&path, perm).unwrap();
    path
}

/// Run `cmd`, return (exit_code, stdout, stderr).
fn run(cmd: &mut Command) -> (i32, String, String) {
    let out = cmd.output().expect("failed to spawn rush-cli");
    (
        out.status.code().unwrap_or(-1),
        String::from_utf8_lossy(&out.stdout).to_string(),
        String::from_utf8_lossy(&out.stderr).to_string(),
    )
}

// ═══════════════════════════════════════════════════════════════════
// -c inline
// ═══════════════════════════════════════════════════════════════════

#[test]
fn dash_c_hello() {
    let (code, stdout, _) = run(Command::new(RUSH).args(["-c", "puts \"hello\""]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "hello");
}

#[test]
fn dash_c_exit_code_propagates_on_error() {
    // Nonexistent command → non-zero exit.
    let (code, _, _) = run(Command::new(RUSH).args(["-c", "zznonexistent_command_xxx"]));
    assert_ne!(code, 0);
}

#[test]
fn dash_c_arithmetic() {
    let (code, stdout, _) = run(Command::new(RUSH).args(["-c", "puts 2 + 3"]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "5");
}

#[test]
fn dash_c_multi_line_array_literal() {
    // ISSUE #252 — `run_script` used to flush the buffered Rush expression
    // at every line, so a multi-line array literal tripped the parser on
    // `roots = [` with no closing bracket. The binary-level fix tracks
    // bracket/brace/paren depth across lines.
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        "roots = [\n\"a\",\n\"b\"\n]\nputs roots.length\n",
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "2");
}

#[test]
fn script_multi_line_array_literal() {
    // Same as the -c test, but exercising the file-execution path so we
    // catch a regression in either invocation mode.
    let dir = scratch_dir("multi_array");
    let script = write_script(
        &dir,
        "arr.rush",
        "#!/usr/bin/env rush\nroots = [\n  \"a\",\n  \"b\"\n]\nputs roots.length\n",
    );
    let (code, stdout, _) = run(Command::new(RUSH).arg(&script));
    let _ = std::fs::remove_dir_all(&dir);
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "2");
}

#[test]
fn script_multi_line_function_call() {
    // #260: splitting a function call across lines at trailing commas
    // inside unmatched parens must not terminate the statement. Same
    // depth-tracking as multi-line arrays — different shape.
    let dir = scratch_dir("multi_call");
    let body = "\
        #!/usr/bin/env rush\n\
        def add3(a, b, c)\n\
          a + b + c\n\
        end\n\
        x = add3(1,\n\
                 2,\n\
                 3)\n\
        puts x\n";
    let script = write_script(&dir, "call.rush", body);
    let (code, stdout, _) = run(Command::new(RUSH).arg(&script));
    let _ = std::fs::remove_dir_all(&dir);
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "6");
}

// ═══════════════════════════════════════════════════════════════════
// Unified pipelines: value sources feeding `|` (#265 Phase 1)
// ═══════════════════════════════════════════════════════════════════

#[test]
fn value_pipe_array_literal_to_count() {
    let (code, stdout, _) = run(Command::new(RUSH).args(["-c", "[1, 2, 3] | count"]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "3");
}

#[test]
fn value_pipe_array_literal_to_first() {
    let (code, stdout, _) = run(Command::new(RUSH).args(["-c", "[10, 20, 30] | first 2"]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "[10, 20]");
}

#[test]
fn value_pipe_bare_variable_to_count() {
    // Bare variable that's a Rush value should route into the value
    // pipeline instead of being dispatched as a shell command (used to
    // error with "rush: arr: command not found").
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        "arr = [1, 2, 3]; arr | count",
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "3");
}

#[test]
fn value_pipe_string_literal_to_count() {
    let (code, stdout, _) = run(Command::new(RUSH).args(["-c", r#""hello" | count"#]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "1");
}

#[test]
fn value_pipe_command_substitution_to_count() {
    let (code, stdout, _) = run(Command::new(RUSH).args(["-c", "$(echo hello) | count"]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "1");
}

#[test]
fn value_pipe_function_call_to_first() {
    // A user-fn call returning a value feeds straight into a value op.
    // `first 2` instead of `first 1` because `first 1` unwraps to the
    // scalar (quirk of apply_first), which would obscure the fact the
    // array flowed through the pipeline.
    let dir = scratch_dir("fn_pipe");
    let body = "\
        #!/usr/bin/env rush\n\
        def ret_arr()\n\
          [10, 20, 30]\n\
        end\n\
        ret_arr() | first 2\n";
    let script = write_script(&dir, "fn_pipe.rush", body);
    let (code, stdout, _) = run(Command::new(RUSH).arg(&script));
    let _ = std::fs::remove_dir_all(&dir);
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "[10, 20]");
}

#[test]
fn value_pipe_unbound_name_still_runs_as_shell() {
    // Regression guard: an unbound identifier must NOT be treated as a
    // value source — it still falls through to shell dispatch. This
    // invocation should exit non-zero because the command doesn't exist.
    let (code, _, _) = run(Command::new(RUSH).args([
        "-c",
        "definitelynotacommand_zxq | head -1",
    ]));
    assert_ne!(code, 0, "unbound name should error at shell dispatch");
}

#[test]
fn value_pipe_string_to_shell_cat() {
    // Scalars serialize as their printed form (no JSON quoting).
    let (code, stdout, _) = run(Command::new(RUSH).args(["-c", r#""hello" | /bin/cat"#]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "hello");
}

#[test]
fn value_pipe_int_array_to_shell_cat() {
    // Arrays of scalars → one element per line (classic Unix shape).
    let (code, stdout, _) = run(Command::new(RUSH).args(["-c", "[1, 2, 3] | /bin/cat"]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim().lines().collect::<Vec<_>>(), vec!["1", "2", "3"]);
}

#[test]
fn value_pipe_hash_to_shell_cat_is_json() {
    // Hashes serialize as single-line JSON — valid for jq, awk, etc.
    let (code, stdout, _) =
        run(Command::new(RUSH).args(["-c", r#"{a: 1, b: "hi"} | /bin/cat"#]));
    assert_eq!(code, 0);
    let trimmed = stdout.trim();
    // Parse the output as JSON and verify content (key order varies).
    let parsed: serde_json::Value =
        serde_json::from_str(trimmed).expect("hash stdin must be valid JSON");
    assert_eq!(parsed["a"], serde_json::json!(1));
    assert_eq!(parsed["b"], serde_json::json!("hi"));
}

#[test]
fn value_pipe_array_of_hashes_to_shell_cat_is_jsonl() {
    // Arrays of hashes serialize as JSON Lines (one JSON doc per line)
    // so jq / awk / grep can consume them record-by-record.
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        r#"[{a: 1}, {a: 2}, {a: 3}] | /bin/cat"#,
    ]));
    assert_eq!(code, 0);
    let lines: Vec<&str> = stdout.trim().lines().collect();
    assert_eq!(lines.len(), 3);
    for (i, line) in lines.iter().enumerate() {
        let parsed: serde_json::Value = serde_json::from_str(line)
            .unwrap_or_else(|e| panic!("line {i} must be valid JSON: {e}: {line:?}"));
        assert_eq!(parsed["a"], serde_json::json!(i + 1));
    }
}

#[test]
fn value_pipe_hash_to_jq_extracts_field() {
    // End-to-end: serialization is real JSON that jq can query.
    if std::process::Command::new("/usr/bin/jq")
        .arg("--version")
        .output()
        .is_err()
    {
        return; // jq not installed; skip
    }
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        r#"{a: 1, b: 2} | /usr/bin/jq ".a""#,
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "1");
}

#[test]
fn shell_then_value_op_still_works() {
    // Regression guard: the classic shell-to-value-op handoff (text
    // output parsed into records) keeps working alongside the new
    // value-source path.
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        "echo hello | count",
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "1");
}

// ═══════════════════════════════════════════════════════════════════
// Mid-chain transitions: the value/shell/value/shell handoffs
// (#265 Phase 3 — regression coverage, not behavior changes)
// ═══════════════════════════════════════════════════════════════════

#[test]
fn pipeline_shell_then_shell_then_value_op() {
    // shell → shell → value-op. Text flows through grep then lands in
    // `count` which sees line-text (one match = one line).
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        // printf not echo -e: BSD echo (macOS) doesn't interpret \n.
        "/usr/bin/printf 'a\\nb\\nc\\n' | grep b | count",
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "1");
}

#[test]
fn pipeline_shell_shell_value_op_shell() {
    // shell → shell → value-op → shell. The value-op's output gets
    // serialized via format_value_for_stdin for the trailing shell stage.
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        "/usr/bin/printf 'a\\nb\\nc\\n' | grep b | first 1 | tr a-z A-Z",
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "B");
}

#[test]
fn pipeline_value_shell_value_op() {
    // value → shell → value-op. Array-of-strings serializes as
    // newline-joined, grep filters, count reads line-text.
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        r#"["a","b","c"] | grep b | count"#,
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "1");
}

#[test]
fn pipeline_value_value_op_shell() {
    // value → value-op → shell. `first 2` stays Value::Array,
    // serialized to newline-joined text for cat.
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        "[1, 2, 3, 4] | first 2 | /bin/cat",
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim().lines().collect::<Vec<_>>(), vec!["1", "2"]);
}

#[test]
fn pipeline_shell_value_op_value_op_shell() {
    // shell → value-op → value-op → shell. Two value-ops in a row
    // keep the Value threaded; the trailing shell serializes once at
    // the boundary.
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        "/usr/bin/printf '1\\n2\\n3\\n4\\n' | first 3 | count | /bin/cat",
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "3");
}

#[test]
fn pipeline_value_hash_to_shell_to_value_op() {
    // value (hash) → shell (pass-through) → value-op. Hash serializes
    // as one-line JSON; cat passes it through; count sees one line.
    let (code, stdout, _) = run(Command::new(RUSH).args([
        "-c",
        r#"{a: 1, b: 2} | /bin/cat | count"#,
    ]));
    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "1");
}

#[test]
fn script_counter_idiom_multi_line() {
    // #261: indexed hash assignment inside a multi-line each block.
    // Exercises parser + triage (for h[k] = v) + run_script's depth
    // tracking simultaneously.
    let dir = scratch_dir("counter");
    let body = "\
        #!/usr/bin/env rush\n\
        counts = {}\n\
        [\"a\", \"b\", \"a\", \"c\", \"a\", \"b\"].each do |x|\n\
          counts[x] = (counts[x] || 0) + 1\n\
        end\n\
        puts counts[\"a\"]\n\
        puts counts[\"b\"]\n\
        puts counts[\"c\"]\n";
    let script = write_script(&dir, "counter.rush", body);
    let (code, stdout, _) = run(Command::new(RUSH).arg(&script));
    let _ = std::fs::remove_dir_all(&dir);
    assert_eq!(code, 0);
    let lines: Vec<&str> = stdout.lines().collect();
    assert_eq!(lines, vec!["3", "2", "1"]);
}

// ═══════════════════════════════════════════════════════════════════
// Script file, invoked via explicit path
// ═══════════════════════════════════════════════════════════════════

#[test]
fn script_explicit_path_runs() {
    let dir = scratch_dir("explicit");
    let script = write_script(
        &dir,
        "hello.rush",
        "#!/usr/bin/env rush\nputs \"ok\"\n",
    );

    let (code, stdout, _) = run(Command::new(RUSH).arg(&script));
    let _ = std::fs::remove_dir_all(&dir);

    assert_eq!(code, 0);
    assert_eq!(stdout.trim(), "ok");
}

#[test]
fn script_argv_accessible() {
    // ARGV inside the script should carry the user args, not the rush
    // binary or the script path.
    let dir = scratch_dir("argv");
    let script = write_script(
        &dir,
        "echo_args.rush",
        "#!/usr/bin/env rush\nfor a in ARGV\n  puts a\nend\n",
    );

    let (code, stdout, _) = run(Command::new(RUSH)
        .arg(&script)
        .args(["first", "second"]));
    let _ = std::fs::remove_dir_all(&dir);

    assert_eq!(code, 0);
    let lines: Vec<&str> = stdout.lines().collect();
    assert_eq!(lines, vec!["first", "second"]);
}

#[test]
fn script_exit_code_propagates() {
    let dir = scratch_dir("exit");
    let script = write_script(
        &dir,
        "fail.rush",
        "#!/usr/bin/env rush\nexit 7\n",
    );

    let (code, _, _) = run(Command::new(RUSH).arg(&script));
    let _ = std::fs::remove_dir_all(&dir);

    assert_eq!(code, 7);
}

// ═══════════════════════════════════════════════════════════════════
// Shebang execution (the script runs itself, not `rush script.rush`)
// ═══════════════════════════════════════════════════════════════════

#[test]
fn shebang_direct_exec() {
    // ISSUE #254: user reported `rename-mmd.rush` exits silently without
    // running when invoked directly from shell. Simulate: place the rush
    // binary as `rush` in a temp dir, put the dir at the front of PATH,
    // write a script with `#!/usr/bin/env rush`, run it via its absolute
    // path, expect it to execute.
    let dir = scratch_dir("shebang");

    // Expose rush-cli as `rush` so the shebang resolves.
    let rush_link = dir.join("rush");
    std::os::unix::fs::symlink(RUSH, &rush_link).unwrap();

    let script = write_script(
        &dir,
        "greet.rush",
        "#!/usr/bin/env rush\nputs \"shebang ok\"\n",
    );

    let path_var = format!(
        "{}:{}",
        dir.display(),
        std::env::var("PATH").unwrap_or_default()
    );
    let mut cmd = Command::new(&script);
    cmd.env("PATH", &path_var);
    let (code, stdout, stderr) = run(&mut cmd);
    let _ = std::fs::remove_dir_all(&dir);

    assert_eq!(
        code, 0,
        "shebang script should exit 0, got {code}; stderr={stderr}"
    );
    assert_eq!(stdout.trim(), "shebang ok");
}

#[test]
fn shebang_via_path_lookup() {
    // ISSUE #254: second user ask — the script must also run when invoked
    // by bare name via PATH (e.g. `my-script.rush`), not just by absolute
    // path. This mirrors how users actually put scripts in ~/bin and call
    // them as commands.
    let dir = scratch_dir("shebang_path");

    let rush_link = dir.join("rush");
    std::os::unix::fs::symlink(RUSH, &rush_link).unwrap();

    let _script = write_script(
        &dir,
        "path-greet.rush",
        "#!/usr/bin/env rush\nputs \"path ok\"\n",
    );

    let path_var = format!(
        "{}:{}",
        dir.display(),
        std::env::var("PATH").unwrap_or_default()
    );
    // Invoke via bare name — PATH resolution should find it.
    let (code, stdout, stderr) = run(Command::new("sh")
        .arg("-c")
        .arg("path-greet.rush")
        .env("PATH", &path_var)
        .stderr(Stdio::piped()));
    let _ = std::fs::remove_dir_all(&dir);

    assert_eq!(
        code, 0,
        "script via PATH should exit 0, got {code}; stderr={stderr}"
    );
    assert_eq!(stdout.trim(), "path ok");
}

// ═══════════════════════════════════════════════════════════════════
// Interpreter invariants
// ═══════════════════════════════════════════════════════════════════

#[test]
fn version_prints_and_exits_zero() {
    let (code, stdout, _) = run(Command::new(RUSH).arg("--version"));
    assert_eq!(code, 0);
    assert!(
        !stdout.trim().is_empty(),
        "--version should print something, got empty"
    );
}
