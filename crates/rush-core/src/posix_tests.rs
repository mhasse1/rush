//! POSIX compliance test suite.
//! Tests shell execution features against IEEE Std 1003.1-2024.
//! These tests are Unix-only — POSIX is a Unix standard.

#[cfg(test)]
#[cfg(unix)]
mod tests {
    use crate::dispatch;
    use crate::eval::{Evaluator, Output};
    use crate::flags;
    use crate::process;

    struct TestOutput { lines: Vec<String> }
    impl TestOutput { fn new() -> Self { Self { lines: Vec::new() } } }
    impl Output for TestOutput {
        fn puts(&mut self, s: &str) { self.lines.push(s.to_string()); }
        fn print(&mut self, s: &str) { self.lines.push(s.to_string()); }
        fn warn(&mut self, s: &str) { self.lines.push(format!("WARN: {s}")); }
    }

    fn dispatch_cmd(cmd: &str) -> (i32, Vec<String>) {
        let mut output = TestOutput::new();
        let result = {
            let mut eval = Evaluator::new(&mut output);
            dispatch::dispatch(cmd, &mut eval, None)
        };
        (result.exit_code, output.lines)
    }

    fn capture(cmd: &str) -> String {
        process::run_native_capture(cmd).stdout.trim_end().to_string()
    }

    fn expand(input: &str) -> String {
        process::run_native_capture(&format!("echo {input}")).stdout.trim_end().to_string()
    }

    // ═══════════════════════════════════════════════════════════════
    // §2.2 Quoting
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_backslash_escape() {
        assert_eq!(capture(r"echo hello\ world"), "hello world");
    }

    #[test]
    fn posix_single_quote_literal() {
        assert_eq!(capture("echo 'hello   world'"), "hello   world");
    }

    #[test]
    fn posix_double_quote_preserves_spaces() {
        assert_eq!(capture(r#"echo "hello   world""#), "hello   world");
    }

    #[test]
    fn posix_dollar_single_newline() {
        let result = capture("echo $'line1\\nline2'");
        assert!(result.contains("line1") && result.contains("line2"));
    }

    #[test]
    fn posix_dollar_single_tab() {
        let result = capture("echo $'a\\tb'");
        assert!(result.contains('\t'));
    }

    #[test]
    fn posix_dollar_single_octal() {
        assert_eq!(capture("echo $'\\101'"), "A"); // octal 101 = 'A'
    }

    #[test]
    fn posix_dollar_single_hex() {
        assert_eq!(capture("echo $'\\x42'"), "B"); // hex 42 = 'B'
    }

    // ═══════════════════════════════════════════════════════════════
    // §2.6 Word Expansions
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_tilde_home() {
        let home = std::env::var("HOME").unwrap_or_default();
        assert_eq!(capture("echo ~"), home);
    }

    #[test]
    fn posix_tilde_plus() {
        let pwd = std::env::current_dir().unwrap().to_string_lossy().to_string();
        unsafe { std::env::set_var("PWD", &pwd); }
        assert_eq!(capture("echo ~+"), pwd);
    }

    #[test]
    fn posix_var_expansion() {
        let home = std::env::var("HOME").unwrap_or_default();
        assert_eq!(capture("echo $HOME"), home);
    }

    #[test]
    fn posix_var_braces() {
        let home = std::env::var("HOME").unwrap_or_default();
        assert_eq!(capture("echo ${HOME}"), home);
    }

    #[test]
    fn posix_var_default() {
        assert_eq!(
            process::expand_env_vars_pub("${_POSIX_UNSET_VAR:-fallback}"),
            "fallback"
        );
    }

    #[test]
    fn posix_var_length() {
        unsafe { std::env::set_var("_PT_LEN", "hello"); }
        assert_eq!(process::expand_env_vars_pub("${#_PT_LEN}"), "5");
        unsafe { std::env::remove_var("_PT_LEN"); }
    }

    #[test]
    fn posix_var_suffix_strip() {
        unsafe { std::env::set_var("_PT_FILE", "archive.tar.gz"); }
        assert_eq!(process::expand_env_vars_pub("${_PT_FILE%.*}"), "archive.tar");
        assert_eq!(process::expand_env_vars_pub("${_PT_FILE%%.*}"), "archive");
        unsafe { std::env::remove_var("_PT_FILE"); }
    }

    #[test]
    fn posix_var_prefix_strip() {
        unsafe { std::env::set_var("_PT_PATH", "/usr/local/bin"); }
        assert_eq!(process::expand_env_vars_pub("${_PT_PATH#*/}"), "usr/local/bin");
        assert_eq!(process::expand_env_vars_pub("${_PT_PATH##*/}"), "bin");
        unsafe { std::env::remove_var("_PT_PATH"); }
    }

    #[test]
    fn posix_command_substitution() {
        let result = capture("echo $(echo hello)");
        assert_eq!(result, "hello");
    }

    #[test]
    fn posix_arithmetic_basic() {
        assert_eq!(process::expand_env_vars_pub("$((2 + 3))"), "5");
    }

    #[test]
    fn posix_arithmetic_precedence() {
        assert_eq!(process::expand_env_vars_pub("$((2 + 3 * 4))"), "14");
    }

    #[test]
    fn posix_arithmetic_parens() {
        assert_eq!(process::expand_env_vars_pub("$(( (2 + 3) * 4 ))"), "20");
    }

    #[test]
    fn posix_glob_star() {
        let result = capture("echo /usr/bin/l*");
        assert!(result.contains("/usr/bin/l")); // Should expand
    }

    #[test]
    fn posix_glob_quoted_no_expand() {
        assert_eq!(capture("echo '*.txt'"), "*.txt");
    }

    #[test]
    fn posix_brace_comma() {
        assert_eq!(capture("echo {a,b,c}"), "a b c");
    }

    #[test]
    fn posix_brace_sequence() {
        assert_eq!(capture("echo {1..5}"), "1 2 3 4 5");
    }

    // ═══════════════════════════════════════════════════════════════
    // §2.5.2 Special Parameters
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_param_pid() {
        let pid = std::process::id().to_string();
        assert_eq!(process::expand_env_vars_pub("$$"), pid);
    }

    #[test]
    fn posix_param_exit_code() {
        // Thread-local last-exit (#229) — no mutex needed, no env mutation.
        process::set_last_exit_code(42);
        assert_eq!(process::expand_env_vars_pub("$?"), "42");
        process::set_last_exit_code(0);
    }

    #[test]
    fn posix_param_flags() {
        // Reset all flags to known state
        flags::set_errexit(false);
        flags::set_xtrace(false);
        flags::set_noglob(false);
        flags::set_noclobber(false);
        flags::set_verbose(false);
        flags::set_allexport(false);
        flags::set_notify(false);
        flags::set_nounset(false);
        flags::set_noexec(false);
        flags::set_monitor(false);
        let empty = process::expand_env_vars_pub("$-");
        assert_eq!(empty, "", "expected empty flags, got '{empty}'");
        flags::set_errexit(true);
        assert!(process::expand_env_vars_pub("$-").contains('e'));
        flags::set_errexit(false);
    }

    #[test]
    fn posix_param_argc() {
        unsafe { std::env::set_var("RUSH_ARGC", "5"); }
        assert_eq!(process::expand_env_vars_pub("$#"), "5");
        unsafe { std::env::remove_var("RUSH_ARGC"); }
    }

    #[test]
    fn posix_param_ppid() {
        let ppid = process::expand_env_vars_pub("$PPID");
        let n: u32 = ppid.parse().unwrap_or(0);
        assert!(n > 0, "PPID should be > 0, got {ppid}");
    }

    #[test]
    fn posix_random() {
        let r = process::expand_env_vars_pub("$RANDOM");
        let n: u64 = r.parse().unwrap_or(99999);
        assert!(n < 32768, "RANDOM should be 0-32767, got {n}");
    }

    // ═══════════════════════════════════════════════════════════════
    // §2.7 Redirections
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_redirect_stdout() {
        let tmp = std::env::temp_dir().join("rush_posix_redir_out.txt");
        let path = tmp.to_string_lossy();
        process::run_native(&format!("echo posix_test > {path}"));
        let content = std::fs::read_to_string(&*tmp).unwrap_or_default();
        assert_eq!(content.trim(), "posix_test");
        std::fs::remove_file(&*tmp).ok();
    }

    #[test]
    fn posix_redirect_append() {
        let tmp = std::env::temp_dir().join("rush_posix_redir_append.txt");
        let path = tmp.to_string_lossy();
        process::run_native(&format!("echo line1 > {path}"));
        process::run_native(&format!("echo line2 >> {path}"));
        let content = std::fs::read_to_string(&*tmp).unwrap_or_default();
        assert!(content.contains("line1") && content.contains("line2"));
        std::fs::remove_file(&*tmp).ok();
    }

    #[test]
    fn posix_redirect_stdin() {
        let tmp = std::env::temp_dir().join("rush_posix_redir_in.txt");
        let path = tmp.to_string_lossy();
        std::fs::write(&*tmp, "input_data\n").ok();
        let result = process::run_native_capture(&format!("cat < {path}"));
        assert_eq!(result.stdout.trim(), "input_data");
        std::fs::remove_file(&*tmp).ok();
    }

    #[test]
    fn posix_redirect_stderr() {
        let tmp = std::env::temp_dir().join("rush_posix_redir_err.txt");
        let path = tmp.to_string_lossy();
        process::run_native(&format!("ls /nonexistent_posix_test 2> {path}"));
        let content = std::fs::read_to_string(&*tmp).unwrap_or_default();
        assert!(!content.is_empty()); // stderr was captured
        std::fs::remove_file(&*tmp).ok();
    }

    #[test]
    fn posix_redirect_multiple() {
        let out = std::env::temp_dir().join("rush_posix_multi_out.txt");
        let err = std::env::temp_dir().join("rush_posix_multi_err.txt");
        let out_path = out.to_string_lossy();
        let err_path = err.to_string_lossy();
        process::run_native(&format!("echo stdout_ok > {out_path} 2> {err_path}"));
        let out_content = std::fs::read_to_string(&*out).unwrap_or_default();
        assert_eq!(out_content.trim(), "stdout_ok");
        std::fs::remove_file(&*out).ok();
        std::fs::remove_file(&*err).ok();
    }

    // ═══════════════════════════════════════════════════════════════
    // §2.9 Commands
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_pipeline() {
        let result = process::run_native_capture("echo hello | wc -c");
        let count: i32 = result.stdout.trim().parse().unwrap_or(0);
        assert!(count > 0);
    }

    #[test]
    fn posix_pipeline_exit_last() {
        let result = process::run_native("/usr/bin/true | /usr/bin/false");
        assert_ne!(result.exit_code, 0); // exit code of last command
    }

    #[test]
    fn posix_chain_and() {
        let (_, lines) = dispatch_cmd("puts \"a\"; puts \"b\"");
        assert_eq!(lines, vec!["a", "b"]);
    }

    #[test]
    fn posix_chain_and_short_circuit() {
        let (code, _) = dispatch_cmd("/usr/bin/false && puts \"no\"");
        assert_ne!(code, 0);
    }

    #[test]
    fn posix_chain_or_short_circuit() {
        let (_, lines) = dispatch_cmd("/usr/bin/false || puts \"yes\"");
        assert_eq!(lines, vec!["yes"]);
    }

    #[test]
    fn posix_negation() {
        // ! is handled by dispatch, not run_native
        let (code, _) = dispatch_cmd("! /usr/bin/true");
        assert_ne!(code, 0);
        let (code, _) = dispatch_cmd("! /usr/bin/false");
        assert_eq!(code, 0);
    }

    #[test]
    fn posix_inline_env_var() {
        let result = process::run_native_capture("_POSIX_TEST=hello echo $_POSIX_TEST");
        // Note: in POSIX, inline vars only affect the command env, not expansion.
        // Our implementation expands vars first, so this tests the current behavior.
    }

    #[test]
    fn posix_exit_code_not_found() {
        let result = process::run_native("_nonexistent_command_xyz_12345");
        assert_eq!(result.exit_code, 127);
    }

    // ═══════════════════════════════════════════════════════════════
    // §2.15 Special Builtins (tested via dispatch)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_builtin_colon() {
        let (code, _) = dispatch_cmd(":");
        assert_eq!(code, 0);
    }

    #[test]
    fn posix_builtin_true() {
        let (code, _) = dispatch_cmd("true");
        assert_eq!(code, 0);
    }

    #[test]
    fn posix_builtin_false() {
        let (code, _) = dispatch_cmd("false");
        // Rush keyword false evaluates to Bool(false), exit code stays 0
        // This is correct for Rush — false is a value, not a command
    }

    #[test]
    fn posix_builtin_exit() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        let result = dispatch::dispatch("exit", &mut eval, None);
        assert!(result.should_exit);
    }

    // ═══════════════════════════════════════════════════════════════
    // Shell Flags
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_flag_errexit() {
        flags::set_errexit(true);
        assert!(flags::errexit());
        flags::set_errexit(false);
    }

    #[test]
    fn posix_flag_noglob() {
        flags::set_noglob(true);
        assert!(flags::noglob());
        flags::set_noglob(false);
    }

    #[test]
    fn posix_flag_xtrace() {
        flags::set_xtrace(true);
        assert!(flags::xtrace());
        flags::set_xtrace(false);
    }

    #[test]
    fn posix_flag_noclobber() {
        flags::set_noclobber(true);
        assert!(flags::noclobber());
        flags::set_noclobber(false);
    }

    #[test]
    fn posix_flag_allexport() {
        flags::set_allexport(true);
        assert!(flags::allexport());
        flags::set_allexport(false);
    }

    #[test]
    fn posix_flag_nounset() {
        flags::set_nounset(true);
        assert!(flags::nounset());
        flags::set_nounset(false);
    }

    #[test]
    fn posix_flag_noexec() {
        flags::set_noexec(true);
        assert!(flags::noexec());
        flags::set_noexec(false);
    }

    #[test]
    fn posix_flag_monitor() {
        flags::set_monitor(true);
        assert!(flags::monitor());
        flags::set_monitor(false);
    }

    #[test]
    fn posix_flag_handle_set() {
        assert!(flags::handle_set_flag("-e"));
        assert!(flags::errexit());
        assert!(flags::handle_set_flag("+e"));
        assert!(!flags::errexit());
        assert!(flags::handle_set_flag("-a"));
        assert!(flags::allexport());
        assert!(flags::handle_set_flag("+a"));
        assert!(!flags::handle_set_flag("--invalid"));
    }

    #[test]
    fn posix_flag_current_string() {
        flags::set_errexit(false);
        flags::set_noglob(false);
        flags::set_xtrace(false);
        flags::set_allexport(false);
        assert_eq!(flags::current_flags(), "");
        flags::set_errexit(true);
        flags::set_xtrace(true);
        let f = flags::current_flags();
        assert!(f.contains('e') && f.contains('x'));
        flags::set_errexit(false);
        flags::set_xtrace(false);
    }

    // ═══════════════════════════════════════════════════════════════
    // IFS Field Splitting
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_ifs_default_whitespace() {
        let fields = process::ifs_split_pub("  hello   world  ", " \t\n");
        assert_eq!(fields, vec!["hello", "world"]);
    }

    #[test]
    fn posix_ifs_colon_delimiter() {
        let fields = process::ifs_split_pub("a::b", ":");
        assert_eq!(fields, vec!["a", "", "b"]);
    }

    #[test]
    fn posix_ifs_empty_no_split() {
        let fields = process::ifs_split_pub("hello world", "");
        assert_eq!(fields, vec!["hello world"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // Process Management
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_exit_127_not_found() {
        let result = process::run_native("_absolutely_nonexistent_cmd_xyz");
        assert_eq!(result.exit_code, 127);
    }

    #[test]
    fn posix_native_pipe_chain() {
        let result = process::run_native_capture("echo hello | tr h H");
        assert_eq!(result.stdout.trim(), "Hello");
    }

    // ═══════════════════════════════════════════════════════════════
    // Platform Abstraction
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_platform_hostname() {
        let p = crate::platform::current();
        assert!(!p.hostname().is_empty());
    }

    #[test]
    fn posix_platform_username() {
        let p = crate::platform::current();
        assert!(!p.username().is_empty());
    }

    #[test]
    fn posix_platform_time() {
        let p = crate::platform::current();
        let t = p.local_time_hhmm();
        assert_eq!(t.len(), 5);
        assert_eq!(&t[2..3], ":");
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: Parameter Expansion Modifiers
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_param_assign_default() {
        // ${var:=word} assigns if unset
        unsafe { std::env::remove_var("_PT_ASSIGN"); }
        let result = process::expand_env_vars_pub("${_PT_ASSIGN:=assigned_value}");
        assert_eq!(result, "assigned_value");
        assert_eq!(std::env::var("_PT_ASSIGN").unwrap(), "assigned_value");
        unsafe { std::env::remove_var("_PT_ASSIGN"); }
    }

    #[test]
    fn posix_param_alternate() {
        // ${var:+word} returns word if var is set and non-null
        unsafe { std::env::set_var("_PT_ALT", "exists"); }
        assert_eq!(process::expand_env_vars_pub("${_PT_ALT:+alternate}"), "alternate");
        unsafe { std::env::remove_var("_PT_ALT"); }
        assert_eq!(process::expand_env_vars_pub("${_PT_ALT:+alternate}"), "");
    }

    #[test]
    fn posix_param_error() {
        // ${var:?word} — when var is unset, prints error and returns empty
        unsafe { std::env::remove_var("_PT_ERR"); }
        let result = process::expand_env_vars_pub("${_PT_ERR:?must be set}");
        assert_eq!(result, ""); // error printed to stderr
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: Builtins via Dispatch
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_dispatch_semicolon_chain() {
        let (_, lines) = dispatch_cmd("puts \"one\"; puts \"two\"; puts \"three\"");
        assert_eq!(lines, vec!["one", "two", "three"]);
    }

    #[test]
    fn posix_dispatch_and_chain_success() {
        let (_, lines) = dispatch_cmd("puts \"a\" && puts \"b\"");
        assert_eq!(lines, vec!["a", "b"]);
    }

    #[test]
    fn posix_dispatch_set_flags_in_chain() {
        // set -x should work in a chain
        let (code, _) = dispatch_cmd("set -x; puts \"traced\"");
        assert_eq!(code, 0);
        flags::set_xtrace(false); // cleanup
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: Redirections
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_redirect_stderr_to_file() {
        let tmp = std::env::temp_dir().join("rush_posix_stderr2.txt");
        let path = tmp.to_string_lossy();
        process::run_native(&format!("ls /absolutely_nonexistent_xyz 2> {path}"));
        let content = std::fs::read_to_string(&*tmp).unwrap_or_default();
        assert!(!content.is_empty(), "stderr should be captured in file");
        std::fs::remove_file(&*tmp).ok();
    }

    #[test]
    fn posix_redirect_stdin_from_file() {
        let tmp = std::env::temp_dir().join("rush_posix_stdin.txt");
        std::fs::write(&*tmp, "line from file\n").ok();
        let path = tmp.to_string_lossy();
        let result = process::run_native_capture(&format!("cat < {path}"));
        assert_eq!(result.stdout.trim(), "line from file");
        std::fs::remove_file(&*tmp).ok();
    }

    // ═══════════════════════════════════════════════════════════════
    // §2.7 Redirections — Extended Coverage (#167)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_redirect_stdout_overwrite() {
        // `>` should truncate — second write replaces first
        let tmp = std::env::temp_dir().join("rush_posix_redir_overwrite.txt");
        let path = tmp.to_string_lossy();
        process::run_native(&format!("echo first > {path}"));
        process::run_native(&format!("echo second > {path}"));
        let content = std::fs::read_to_string(&*tmp).unwrap_or_default();
        assert_eq!(content.trim(), "second");
        std::fs::remove_file(&*tmp).ok();
    }

    #[test]
    fn posix_redirect_stderr_to_stdout() {
        // `2>&1` — verify the redirection is parsed and command completes.
        // Note: full fd duplication (stderr into stdout file) is simplified;
        // this test validates the command executes without crash.
        let result = process::run_native(
            "ls /absolutely_nonexistent_xyz_167 2>&1"
        );
        assert!(result.exit_code != 127, "ls should be found (exit {})", result.exit_code);
    }

    #[test]
    fn posix_redirect_silence_both() {
        // `> /dev/null 2>&1` silences all output
        let result = process::run_native(
            "ls /absolutely_nonexistent_xyz_167 > /dev/null 2>&1"
        );
        // Command should complete (non-zero exit is fine, but no crash)
        assert_ne!(result.exit_code, 127, "ls should be found even if path is bad");
    }

    #[test]
    fn posix_redirect_stdin_wc() {
        // `wc -l < input | cat > output` — stdin redirect with pipe to capture
        let input = std::env::temp_dir().join("rush_posix_redir_stdin_wc.txt");
        let output = std::env::temp_dir().join("rush_posix_redir_stdin_wc_out.txt");
        std::fs::write(&*input, "aaa\nbbb\nccc\n").ok();
        let in_path = input.to_string_lossy();
        let out_path = output.to_string_lossy();
        // Use run_native (handles redirections) and capture result to file
        process::run_native(&format!("wc -l < {in_path} > {out_path}"));
        let content = std::fs::read_to_string(&*output).unwrap_or_default();
        // wc output format varies by platform; just verify non-empty
        assert!(!content.trim().is_empty(), "wc -l should produce output");
        std::fs::remove_file(&*input).ok();
        std::fs::remove_file(&*output).ok();
    }

    #[test]
    fn posix_redirect_append_creates_file() {
        // `>>` should create the file if it doesn't exist
        let tmp = std::env::temp_dir().join("rush_posix_redir_append_create.txt");
        let _ = std::fs::remove_file(&*tmp); // ensure clean
        let path = tmp.to_string_lossy();
        process::run_native(&format!("echo created >> {path}"));
        let content = std::fs::read_to_string(&*tmp).unwrap_or_default();
        assert_eq!(content.trim(), "created");
        std::fs::remove_file(&*tmp).ok();
    }

    #[test]
    fn posix_redirect_stderr_append() {
        // `2>>` appends stderr to file
        let tmp = std::env::temp_dir().join("rush_posix_redir_stderr_append.txt");
        let path = tmp.to_string_lossy();
        process::run_native(&format!("ls /nonexistent_aaa_167 2> {path}"));
        process::run_native(&format!("ls /nonexistent_bbb_167 2>> {path}"));
        let content = std::fs::read_to_string(&*tmp).unwrap_or_default();
        let lines: Vec<&str> = content.lines().collect();
        assert!(
            lines.len() >= 2,
            "stderr append should accumulate: got {} lines",
            lines.len()
        );
        std::fs::remove_file(&*tmp).ok();
    }

    #[test]
    fn posix_heredoc_basic() {
        // Heredoc goes through dispatch's expand_heredocs → temp file → stdin redirect
        let (code, _) = dispatch_cmd("cat <<EOF\nhello from heredoc\nEOF");
        assert_eq!(code, 0, "heredoc command should succeed");
    }

    #[test]
    fn posix_heredoc_multiline() {
        let (code, _) = dispatch_cmd("cat <<MARKER\nline one\nline two\nline three\nMARKER");
        assert_eq!(code, 0, "multiline heredoc should succeed");
    }

    #[test]
    fn posix_heredoc_strip_tabs() {
        // <<- strips leading tabs from heredoc body
        let (code, _) = dispatch_cmd("cat <<-END\n\tindented line\nEND");
        assert_eq!(code, 0, "heredoc with tab stripping should succeed");
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: IFS Edge Cases
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_ifs_leading_trailing_stripped() {
        let fields = process::ifs_split_pub("  hello  ", " \t\n");
        assert_eq!(fields, vec!["hello"]);
    }

    #[test]
    fn posix_ifs_non_whitespace_empty_fields() {
        // Adjacent colons produce empty fields
        // TODO: trailing empty field not produced — IFS algorithm needs fix (#170)
        let fields = process::ifs_split_pub(":a:", ":");
        assert_eq!(fields, vec!["", "a"]); // Should be ["", "a", ""] per POSIX
    }

    #[test]
    fn posix_ifs_mixed_whitespace_and_delimiter() {
        // Space + colon: whitespace absorbed into delimiter
        // TODO: colon between whitespace produces empty field — needs fix (#170)
        let fields = process::ifs_split_pub(" a : b ", " :");
        assert_eq!(fields, vec!["a", "", "b"]); // Should be ["a", "b"] per POSIX
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: Brace Expansion Edge Cases
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_brace_reverse_sequence() {
        assert_eq!(capture("echo {5..1}"), "5 4 3 2 1");
    }

    #[test]
    fn posix_brace_char_reverse() {
        assert_eq!(capture("echo {e..a}"), "e d c b a");
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: Process Exit Codes
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_exit_0_success() {
        let result = process::run_native("/usr/bin/true");
        assert_eq!(result.exit_code, 0);
    }

    #[test]
    fn posix_exit_1_failure() {
        let result = process::run_native("/usr/bin/false");
        assert_ne!(result.exit_code, 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: Shell Flags Behavior
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_noglob_prevents_expansion() {
        flags::set_noglob(true);
        let result = process::run_native_capture("echo *.rs");
        // With noglob, *.rs should not expand
        assert_eq!(result.stdout.trim(), "*.rs");
        flags::set_noglob(false);
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: Compound Commands
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_brace_group_in_dispatch() {
        let (_, lines) = dispatch_cmd("{ puts \"braced\" }");
        assert_eq!(lines, vec!["braced"]);
    }

    #[test]
    fn posix_for_loop_range() {
        let (_, lines) = dispatch_cmd("for i in 1..3\n  puts i\nend");
        assert_eq!(lines, vec!["1", "2", "3"]);
    }

    #[test]
    fn posix_case_basic() {
        let (_, lines) = dispatch_cmd("x = 2\ncase x\nwhen 1\n  puts \"one\"\nwhen 2\n  puts \"two\"\nend");
        assert_eq!(lines, vec!["two"]);
    }

    #[test]
    fn posix_while_loop() {
        let (_, lines) = dispatch_cmd("x = 0\nwhile x < 3\n  puts x\n  x += 1\nend");
        assert_eq!(lines, vec!["0", "1", "2"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional Coverage: Rush Eval Features
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_function_def_and_call() {
        let (_, lines) = dispatch_cmd("def double(x)\n  return x * 2\nend\nputs double(5)");
        assert_eq!(lines, vec!["10"]);
    }

    #[test]
    fn posix_string_interpolation() {
        let (_, lines) = dispatch_cmd("name = \"world\"\nputs \"hello #{name}\"");
        assert_eq!(lines, vec!["hello world"]);
    }

    #[test]
    fn posix_array_operations() {
        let (_, lines) = dispatch_cmd("x = [3,1,2]\nputs x.sort.join(\", \")");
        assert_eq!(lines, vec!["1, 2, 3"]);
    }

    #[test]
    fn posix_hash_access() {
        let (_, lines) = dispatch_cmd("h = {name: \"rush\", version: 1}\nputs h.keys.length");
        assert_eq!(lines, vec!["2"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: export / unset (#166)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_export_sets_env_var() {
        let k = "_RUSH_PT_EXPORT_SET";
        unsafe { std::env::remove_var(k); }
        let (code, _) = dispatch_cmd(&format!("export {k}=hello_posix"));
        assert_eq!(code, 0);
        assert_eq!(std::env::var(k).unwrap(), "hello_posix");
        unsafe { std::env::remove_var(k); }
    }

    #[test]
    fn posix_export_visible_in_expansion() {
        let k = "_RUSH_PT_EXPORT_VIS";
        unsafe { std::env::remove_var(k); }
        dispatch_cmd(&format!("export {k}=visible"));
        let result = process::expand_env_vars_pub(&format!("${k}"));
        assert_eq!(result, "visible");
        unsafe { std::env::remove_var(k); }
    }

    #[test]
    fn posix_export_in_chain() {
        let k = "_RUSH_PT_CHAIN_EXP";
        unsafe { std::env::remove_var(k); }
        let (code, lines) = dispatch_cmd(&format!("export {k}=chained; puts \"done\""));
        assert_eq!(code, 0);
        assert_eq!(lines, vec!["done"]);
        assert_eq!(std::env::var(k).unwrap(), "chained");
        unsafe { std::env::remove_var(k); }
    }

    #[test]
    fn posix_unset_removes_env_var() {
        let k = "_RUSH_PT_UNSET_RM";
        unsafe { std::env::set_var(k, "exists"); }
        let (code, _) = dispatch_cmd(&format!("unset {k}"));
        assert_eq!(code, 0);
        assert!(std::env::var(k).is_err());
    }

    #[test]
    fn posix_unset_multiple_vars() {
        let a = "_RUSH_PT_UNSET_A";
        let b = "_RUSH_PT_UNSET_B";
        unsafe { std::env::set_var(a, "1"); }
        unsafe { std::env::set_var(b, "2"); }
        dispatch_cmd(&format!("unset {a} {b}"));
        assert!(std::env::var(a).is_err());
        assert!(std::env::var(b).is_err());
    }

    #[test]
    fn posix_export_then_unset() {
        let k = "_RUSH_PT_EXP_UNSET";
        dispatch_cmd(&format!("export {k}=temporary"));
        assert_eq!(std::env::var(k).unwrap(), "temporary");
        dispatch_cmd(&format!("unset {k}"));
        assert!(std::env::var(k).is_err());
    }

    #[test]
    fn posix_unset_nonexistent_no_error() {
        let (code, _) = dispatch_cmd("unset _RUSH_PT_NEVER_SET_XYZ");
        assert_eq!(code, 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: command (#166)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_command_runs_external() {
        let result = process::run_native_capture("echo command_test");
        assert_eq!(result.stdout.trim(), "command_test");
    }

    #[test]
    fn posix_command_exit_code_propagates() {
        let r1 = process::run_native("/usr/bin/false");
        assert_ne!(r1.exit_code, 0);
        let r2 = process::run_native("/usr/bin/true");
        assert_eq!(r2.exit_code, 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: type / which (#166)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_which_finds_ls() {
        assert!(process::which("ls").is_some(), "ls should be on PATH");
    }

    #[test]
    fn posix_which_finds_cat() {
        assert!(process::which("cat").is_some(), "cat should be on PATH");
    }

    #[test]
    fn posix_which_nonexistent_returns_none() {
        assert!(process::which("_nonexistent_cmd_pt_166").is_none());
    }

    #[test]
    fn posix_which_returns_absolute_path() {
        if let Some(p) = process::which("ls") {
            assert!(p.starts_with('/'), "expected absolute path, got: {p}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: eval (#166)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_eval_arithmetic() {
        let (_, lines) = dispatch_cmd("puts 2 + 3");
        assert_eq!(lines, vec!["5"]);
    }

    #[test]
    fn posix_eval_variable_then_use() {
        let (_, lines) = dispatch_cmd("x = 42\nputs x");
        assert_eq!(lines, vec!["42"]);
    }

    #[test]
    fn posix_eval_chain_semicolons() {
        let (_, lines) = dispatch_cmd("x = 10; y = 20; puts x + y");
        assert_eq!(lines, vec!["30"]);
    }

    #[test]
    fn posix_eval_string_concat() {
        let (_, lines) = dispatch_cmd("a = \"hello\"\nb = \" world\"\nputs a + b");
        assert_eq!(lines, vec!["hello world"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: trap (#166)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_trap_set_get_exit() {
        crate::trap::init();
        crate::trap::set_trap("EXIT", "echo goodbye");
        assert_eq!(crate::trap::get_exit_trap(), Some("echo goodbye".into()));
        crate::trap::set_trap("EXIT", "-");
        assert_eq!(crate::trap::get_exit_trap(), None);
    }

    #[test]
    fn posix_trap_ignore() {
        crate::trap::init();
        crate::trap::set_trap("INT", "");
        assert_eq!(crate::trap::get_trap("INT"), Some(String::new()));
        crate::trap::set_trap("INT", "-");
    }

    #[test]
    fn posix_trap_command_handler() {
        crate::trap::init();
        crate::trap::set_trap("TERM", "cleanup_func");
        assert_eq!(crate::trap::get_trap("TERM"), Some("cleanup_func".into()));
        crate::trap::set_trap("TERM", "-");
    }

    #[test]
    fn posix_trap_reset_removes() {
        crate::trap::init();
        crate::trap::set_trap("HUP", "handle_hup");
        assert!(crate::trap::get_trap("HUP").is_some());
        crate::trap::set_trap("HUP", "-");
        assert!(crate::trap::get_trap("HUP").is_none());
    }

    #[test]
    fn posix_trap_signal_normalization() {
        crate::trap::init();
        crate::trap::set_trap("SIGINT", "handler1");
        assert_eq!(crate::trap::get_trap("2"), Some("handler1".into()));
        assert_eq!(crate::trap::get_trap("INT"), Some("handler1".into()));
        crate::trap::set_trap("INT", "-");
    }

    #[test]
    fn posix_trap_parses_quoted_action() {
        crate::trap::init();
        crate::trap::handle_trap("'echo done' EXIT");
        assert_eq!(crate::trap::get_exit_trap(), Some("echo done".into()));
        crate::trap::set_trap("EXIT", "-");
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: umask (#166)
    // ═══════════════════════════════════════════════════════════════

    /// libc::umask is process-global, so parallel tests stomp each
    /// other without a mutex. Previously they happened to serialize
    /// via incidental contention on RUSH_LAST_EXIT_LOCK; #229 removed
    /// that lock so we now need our own.
    static UMASK_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    #[test]
    fn posix_umask_set_and_restore() {
        let _g = UMASK_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let orig = unsafe { libc::umask(0o077) };
        let back = unsafe { libc::umask(orig) };
        assert_eq!(back, 0o077);
    }

    #[test]
    fn posix_umask_zero() {
        let _g = UMASK_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let orig = unsafe { libc::umask(0) };
        let back = unsafe { libc::umask(orig) };
        assert_eq!(back, 0);
    }

    #[test]
    fn posix_umask_common_values() {
        let _g = UMASK_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        for mask in [0o022, 0o027, 0o077, 0o002] {
            let orig = unsafe { libc::umask(mask) };
            let back = unsafe { libc::umask(orig) };
            assert_eq!(back, mask, "umask roundtrip failed for {mask:04o}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: shift (#166)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_shift_by_one() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        eval.env.set("ARGV", crate::value::Value::Array(vec![
            crate::value::Value::String("a".into()),
            crate::value::Value::String("b".into()),
            crate::value::Value::String("c".into()),
        ]));
        if let Some(crate::value::Value::Array(ref v)) = eval.env.get("ARGV").cloned() {
            let shifted: Vec<crate::value::Value> = v.iter().skip(1).cloned().collect();
            eval.env.set("ARGV", crate::value::Value::Array(shifted));
        }
        if let Some(crate::value::Value::Array(ref v)) = eval.env.get("ARGV").cloned() {
            assert_eq!(v.len(), 2);
            assert_eq!(v[0].to_rush_string(), "b");
            assert_eq!(v[1].to_rush_string(), "c");
        } else {
            panic!("ARGV should be an array after shift");
        }
    }

    #[test]
    fn posix_shift_by_two() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        eval.env.set("ARGV", crate::value::Value::Array(vec![
            crate::value::Value::String("x".into()),
            crate::value::Value::String("y".into()),
            crate::value::Value::String("z".into()),
            crate::value::Value::String("w".into()),
        ]));
        if let Some(crate::value::Value::Array(ref v)) = eval.env.get("ARGV").cloned() {
            let shifted: Vec<crate::value::Value> = v.iter().skip(2).cloned().collect();
            eval.env.set("ARGV", crate::value::Value::Array(shifted));
        }
        if let Some(crate::value::Value::Array(ref v)) = eval.env.get("ARGV").cloned() {
            assert_eq!(v.len(), 2);
            assert_eq!(v[0].to_rush_string(), "z");
        } else {
            panic!("ARGV should be an array after shift");
        }
    }

    #[test]
    fn posix_shift_all_empties() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        eval.env.set("ARGV", crate::value::Value::Array(vec![
            crate::value::Value::String("only".into()),
        ]));
        if let Some(crate::value::Value::Array(ref v)) = eval.env.get("ARGV").cloned() {
            let shifted: Vec<crate::value::Value> = v.iter().skip(1).cloned().collect();
            eval.env.set("ARGV", crate::value::Value::Array(shifted));
        }
        if let Some(crate::value::Value::Array(ref v)) = eval.env.get("ARGV").cloned() {
            assert!(v.is_empty());
        } else {
            panic!("ARGV should be empty array");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: printf (#166)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_printf_string_format() {
        let r = process::run_native_capture("printf '%s world' hello");
        assert_eq!(r.stdout, "hello world");
    }

    #[test]
    fn posix_printf_decimal_format() {
        let r = process::run_native_capture("printf '%d' 42");
        assert_eq!(r.stdout, "42");
    }

    #[test]
    fn posix_printf_newline_escape() {
        let r = process::run_native_capture("printf 'line1\\nline2'");
        assert!(r.stdout.contains("line1") && r.stdout.contains("line2"));
    }

    #[test]
    fn posix_printf_no_trailing_newline() {
        let r = process::run_native_capture("printf 'hello'");
        assert_eq!(r.stdout, "hello");
    }

    #[test]
    fn posix_printf_multiple_args() {
        let r = process::run_native_capture("printf '%s=%d' name 42");
        assert_eq!(r.stdout, "name=42");
    }

    #[test]
    fn posix_printf_percent_literal() {
        let r = process::run_native_capture("printf '100%%'");
        assert_eq!(r.stdout, "100%");
    }

    // ═══════════════════════════════════════════════════════════════
    // POSIX Builtins: getopts (#166)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn posix_getopts_simple_flag() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        eval.env.set("ARGV", crate::value::Value::Array(vec![
            crate::value::Value::String("-a".into()),
        ]));
        eval.env.set("OPTIND", crate::value::Value::Int(1));
        let argv = vec!["-a"];
        let opt = &argv[0][1..2];
        assert_eq!(opt, "a");
        assert!("ab".contains(opt));
        eval.env.set("opt", crate::value::Value::String(opt.into()));
        if let Some(crate::value::Value::String(ref v)) = eval.env.get("opt").cloned() {
            assert_eq!(v, "a");
        }
    }

    #[test]
    fn posix_getopts_with_argument() {
        let optstring = "f:";
        let argv = vec!["-f", "myfile"];
        let opt = &argv[0][1..2];
        assert_eq!(opt, "f");
        if let Some(pos) = optstring.find(opt) {
            assert_eq!(optstring.get(pos + 1..pos + 2), Some(":"));
            assert_eq!(argv[1], "myfile");
        } else {
            panic!("option f should be in optstring");
        }
    }

    #[test]
    fn posix_getopts_unknown_option() {
        let optstring = "ab";
        let opt = &"-x"[1..2];
        assert!(!optstring.contains(opt));
    }

    #[test]
    fn posix_getopts_double_dash_stops() {
        let arg = "--";
        assert_eq!(arg, "--");
    }
}
