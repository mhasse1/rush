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
