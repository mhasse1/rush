//! Script-surface integration tests.
//!
//! Each test here is a tiny Rush program run through `dispatch::dispatch`
//! with expected output (or expected exit code, or both). The goal is
//! coverage of the *language as a user sees it* — if a test here fails,
//! a real `.rush` script a user wrote probably breaks the same way.
//!
//! Tests are grouped by language surface (literals, control flow, strings,
//! shell interop, ...). Known-broken surface has an ISSUE: tag in the
//! comment and fails the test on purpose — the test goes green when the
//! underlying bug is fixed, so CI tells us when a fix landed without a
//! manual recheck.

#![cfg(test)]

use crate::dispatch;
use crate::eval::{Evaluator, Output};

struct CaptureOutput {
    lines: Vec<String>,
}
impl CaptureOutput {
    fn new() -> Self {
        Self { lines: Vec::new() }
    }
}
impl Output for CaptureOutput {
    fn puts(&mut self, s: &str) {
        self.lines.push(s.to_string());
    }
    fn print(&mut self, s: &str) {
        self.lines.push(s.to_string());
    }
    fn warn(&mut self, s: &str) {
        self.lines.push(format!("WARN: {s}"));
    }
}

/// Run a rush program through dispatch; returns (exit_code, stdout_lines).
fn run(program: &str) -> (i32, Vec<String>) {
    let mut output = CaptureOutput::new();
    let result = {
        let mut eval = Evaluator::new(&mut output);
        dispatch::dispatch(program, &mut eval, None)
    };
    (result.exit_code, output.lines)
}

/// Assert that `program` executes cleanly and emits `expected` as the
/// concatenated output lines. Prints a diff-style failure if not.
#[track_caller]
fn expect_output(program: &str, expected: &str) {
    let (code, lines) = run(program);
    let joined = lines.join("\n");
    assert_eq!(
        joined, expected,
        "\n— program —\n{program}\n— expected —\n{expected}\n— got (exit={code}) —\n{joined}\n"
    );
}

// ═══════════════════════════════════════════════════════════════════
// Literals and basic values
// ═══════════════════════════════════════════════════════════════════

#[test]
fn literal_int() {
    expect_output("puts 42", "42");
}

#[test]
fn literal_float() {
    expect_output("puts 3.14", "3.14");
}

#[test]
fn literal_string() {
    expect_output(r#"puts "hello""#, "hello");
}

#[test]
fn string_interpolation_basic() {
    expect_output(r##"name = "world"; puts "hello #{name}""##, "hello world");
}

#[test]
fn string_interpolation_expression() {
    expect_output(r##"x = 2; y = 3; puts "sum=#{x + y}""##, "sum=5");
}

#[test]
fn single_quoted_string_no_interpolation() {
    expect_output(r##"name = "world"; puts 'hello #{name}'"##, "hello #{name}");
}

// ═══════════════════════════════════════════════════════════════════
// Arrays
// ═══════════════════════════════════════════════════════════════════

#[test]
fn array_literal_single_line() {
    expect_output("arr = [1, 2, 3]; puts arr.length", "3");
}

#[test]
fn array_multi_line_literal() {
    // ISSUE #252: multi-line array literals fail parse ("Expected RBracket, got Eof").
    // Idiomatic formatting for real scripts; should parse the same as the
    // one-line form.
    let program = "\
        roots = [\n\
          \"a\",\n\
          \"b\"\n\
        ]\n\
        puts roots.length\n";
    expect_output(program, "2");
}

#[test]
fn array_indexing_positive() {
    expect_output("a = [10, 20, 30]; puts a[1]", "20");
}

#[test]
fn array_indexing_negative() {
    expect_output("a = [10, 20, 30]; puts a[-1]", "30");
}

#[test]
fn array_first_last() {
    expect_output("a = [5, 6, 7]; puts a.first; puts a.last", "5\n7");
}

#[test]
fn array_sum() {
    expect_output("puts [1, 2, 3, 4].sum", "10");
}

#[test]
fn array_map_block() {
    expect_output("puts [1, 2, 3].map { |x| x * 2 }.sum", "12");
}

#[test]
fn array_select_block() {
    expect_output(
        "puts [1, 2, 3, 4, 5].select { |x| x > 2 }.length",
        "3",
    );
}

// ═══════════════════════════════════════════════════════════════════
// Hashes
// ═══════════════════════════════════════════════════════════════════

#[test]
fn hash_literal_and_bracket_access() {
    expect_output(r#"h = {name: "ann", age: 30}; puts h["name"]"#, "ann");
}

#[test]
fn hash_dot_access() {
    expect_output(r#"h = {name: "ann"}; puts h.name"#, "ann");
}

#[test]
fn hash_length() {
    expect_output("puts {a: 1, b: 2, c: 3}.length", "3");
}

// ═══════════════════════════════════════════════════════════════════
// Strings
// ═══════════════════════════════════════════════════════════════════

#[test]
fn string_upcase() {
    expect_output(r#"puts "hello".upcase"#, "HELLO");
}

#[test]
fn string_length() {
    expect_output(r#"puts "hello".length"#, "5");
}

#[test]
fn string_strip() {
    expect_output(r#"puts "  spaced  ".strip"#, "spaced");
}

#[test]
fn string_replace_literal() {
    expect_output(
        r#"puts "foo.mmd".replace(".mmd", ".mermaid")"#,
        "foo.mermaid",
    );
}

#[test]
fn string_sub_with_regex_replaces_first_match() {
    // ISSUE #256: .sub(/regex/, repl) returns empty string today.
    // Expected: first regex match replaced, rest of string intact.
    expect_output(
        r#"puts "foo.mmd".sub(/\.mmd$/, ".mermaid")"#,
        "foo.mermaid",
    );
}

#[test]
fn string_gsub_with_regex_replaces_all_matches() {
    // ISSUE #256: .gsub(/regex/, repl) splices the replacement between
    // every character of the input today. Expected: all non-overlapping
    // regex matches replaced.
    expect_output(r#"puts "aaa".gsub(/a/, "b")"#, "bbb");
}

#[test]
fn string_lines() {
    expect_output(
        r#"s = "a\nb\nc"; puts s.lines.length"#,
        "3",
    );
}

#[test]
fn string_color_method_preserves_content() {
    // ISSUE #253: "text".green produces empty output via puts today.
    // Whether color codes are applied depends on TTY / NO_COLOR, but the
    // content must survive the method call.
    let (_, lines) = run(r#"puts "hello".green"#);
    let joined = lines.join("\n");
    assert!(
        joined.contains("hello"),
        "expected 'hello' somewhere in output, got: {joined:?}"
    );
}

#[test]
fn string_color_method_chains_to_length() {
    // ISSUE #253: "hello".green.length crashes dispatch today
    // ("puts: command not found") because the parser re-dispatches the
    // whole line as an external command. Method chaining on the return
    // value of .green should work like any other string method.
    expect_output(r#"puts "hello".green.length"#, "5");
}

// ═══════════════════════════════════════════════════════════════════
// Control flow
// ═══════════════════════════════════════════════════════════════════

#[test]
fn if_else_end_taken_branch() {
    expect_output(
        "x = 10\nif x > 5\n  puts \"big\"\nelse\n  puts \"small\"\nend",
        "big",
    );
}

#[test]
fn if_else_end_else_branch() {
    expect_output(
        "x = 1\nif x > 5\n  puts \"big\"\nelse\n  puts \"small\"\nend",
        "small",
    );
}

#[test]
fn unless_end() {
    expect_output(
        "x = 1\nunless x > 5\n  puts \"small\"\nend",
        "small",
    );
}

#[test]
fn for_in_array() {
    expect_output(
        "for x in [1, 2, 3]\n  puts x\nend",
        "1\n2\n3",
    );
}

#[test]
fn while_loop() {
    expect_output(
        "i = 0\nwhile i < 3\n  puts i\n  i += 1\nend",
        "0\n1\n2",
    );
}

#[test]
fn ternary() {
    expect_output("x = 7; puts (x > 5 ? \"big\" : \"small\")", "big");
}

#[test]
fn postfix_if() {
    expect_output(r#"x = 10; puts "big" if x > 5"#, "big");
}

// ═══════════════════════════════════════════════════════════════════
// Functions
// ═══════════════════════════════════════════════════════════════════

#[test]
fn function_def_and_call() {
    expect_output(
        "def greet(name)\n  return \"hi #{name}\"\nend\nputs greet(\"ann\")",
        "hi ann",
    );
}

#[test]
fn function_implicit_return() {
    expect_output(
        "def square(x)\n  x * x\nend\nputs square(7)",
        "49",
    );
}

#[test]
fn function_default_arg() {
    expect_output(
        "def greet(name = \"stranger\")\n  return name\nend\nputs greet()",
        "stranger",
    );
}

// ═══════════════════════════════════════════════════════════════════
// Shell interop
// ═══════════════════════════════════════════════════════════════════

#[test]
fn command_substitution_basic() {
    expect_output(r#"x = $(echo hello); puts x"#, "hello");
}

#[test]
fn interpolation_inside_command_substitution() {
    // ISSUE #255: #{expr} inside $() is passed through literally today.
    // Expected: rush-side interpolation runs before the subshell sees
    // the command, so echo receives the expanded text.
    expect_output(
        r##"msg = "hello"; x = $(echo "#{msg}"); puts "got=[#{x}]""##,
        "got=[hello]",
    );
}

#[test]
fn interpolation_inside_export_value() {
    // ISSUE #255: export T="#{...}" passes the literal #{...} to the
    // environment today. Expected: rush interpolates first, env sees
    // the expanded value.
    expect_output(
        r##"target = "/tmp"; export T="#{target}"; puts env.T"##,
        "/tmp",
    );
}

#[test]
fn shell_pipe_on_command_output() {
    expect_output(r#"x = $(echo hello | tr a-z A-Z); puts x"#, "HELLO");
}

// ═══════════════════════════════════════════════════════════════════
// Stdlib
// ═══════════════════════════════════════════════════════════════════

#[test]
fn dir_list_recurse_returns_structured_paths() {
    // ISSUE #257: Dir.list(:recurse) returns bare basenames with no
    // parent prefix, making it impossible to reconstruct full paths
    // from the output. Expected: full paths, or paths relative to the
    // root, or file-like objects with a .path member — whichever shape
    // rush commits to, each entry must encode the parent relationship.
    let root = std::env::temp_dir().join(format!(
        "rush_dirlist_{}_{}",
        std::process::id(),
        rand_suffix()
    ));
    std::fs::create_dir_all(root.join("sub/deeper")).unwrap();
    std::fs::write(root.join("a.txt"), "").unwrap();
    std::fs::write(root.join("sub/b.txt"), "").unwrap();
    std::fs::write(root.join("sub/deeper/c.txt"), "").unwrap();

    let program = format!(
        r#"Dir.list("{}", :recurse).each {{ |f| puts f }}"#,
        root.display()
    );
    let (_, lines) = run(&program);

    // The c.txt entry is the deepest file — its output must contain
    // enough path information to locate it (e.g. "sub/deeper/c.txt"
    // or the absolute form). Bare "c.txt" is insufficient.
    let found = lines.iter().find(|l| l.contains("c.txt")).cloned();
    std::fs::remove_dir_all(&root).ok();

    let found = found.expect("recurse output should include c.txt entry");
    assert!(
        found.contains("deeper") || found.contains("sub"),
        "recurse entry for c.txt should carry parent path; got bare: {found:?}"
    );
}

#[test]
fn file_read_write_roundtrip() {
    let path = std::env::temp_dir().join(format!(
        "rush_rw_{}_{}.txt",
        std::process::id(),
        rand_suffix()
    ));
    let program = format!(
        r#"File.write("{0}", "hello"); puts File.read("{0}")"#,
        path.display()
    );
    expect_output(&program, "hello");
    std::fs::remove_file(&path).ok();
}

#[test]
fn dir_exist_false_for_nonexistent() {
    expect_output(
        r#"puts Dir.exist?("/nonexistent_path_for_test_xyz")"#,
        "false",
    );
}

// ═══════════════════════════════════════════════════════════════════
// Ranges / misc
// ═══════════════════════════════════════════════════════════════════

#[test]
fn range_inclusive() {
    expect_output("puts (1..3).to_a.length", "3");
}

#[test]
fn range_exclusive() {
    expect_output("puts (1...3).to_a.length", "2");
}

#[test]
fn dollar_question_last_exit_code() {
    expect_output(r#"true; puts $?"#, "0");
}

// ═══════════════════════════════════════════════════════════════════
// helper
// ═══════════════════════════════════════════════════════════════════

/// Cheap per-test suffix so parallel tests don't collide on temp paths.
fn rand_suffix() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    let n = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.subsec_nanos())
        .unwrap_or(0);
    format!("{n:x}")
}
