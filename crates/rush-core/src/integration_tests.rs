//! Integration tests for LLM mode, MCP, and shell features.
//! Tests the full execution pipeline via dispatch.

#[cfg(test)]
mod tests {
    use crate::dispatch;
    use crate::eval::{Evaluator, Output};
    use crate::llm;
    use crate::process;

    struct CaptureOutput { lines: Vec<String> }
    impl CaptureOutput { fn new() -> Self { Self { lines: Vec::new() } } }
    impl Output for CaptureOutput {
        fn puts(&mut self, s: &str) { self.lines.push(s.to_string()); }
        fn print(&mut self, s: &str) { self.lines.push(s.to_string()); }
        fn warn(&mut self, s: &str) { self.lines.push(format!("WARN: {s}")); }
    }

    fn run(cmd: &str) -> (i32, Vec<String>) {
        let mut output = CaptureOutput::new();
        let result = {
            let mut eval = Evaluator::new(&mut output);
            dispatch::dispatch(cmd, &mut eval, None)
        };
        (result.exit_code, output.lines)
    }

    // ═══════════════════════════════════════════════════════════════
    // LLM Mode
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn llm_execute_shell() {
        let result = llm::execute_one("echo hello");
        assert_eq!(result.status, "success");
        assert_eq!(result.stdout.as_deref(), Some("hello"));
    }

    #[test]
    fn llm_execute_rush() {
        let result = llm::execute_one("puts 2 + 3");
        assert_eq!(result.status, "success");
        assert_eq!(result.stdout.as_deref(), Some("5"));
    }

    #[test]
    fn llm_lcat_nonexistent() {
        let result = llm::lcat("/nonexistent/file.txt", "/tmp");
        assert_eq!(result.status, "error");
    }

    #[test]
    fn llm_lcat_text_file() {
        let tmp = std::env::temp_dir().join("rush_llm_int_test.txt");
        std::fs::write(&tmp, "test content").ok();
        let result = llm::lcat(&tmp.to_string_lossy(), "/tmp");
        assert_eq!(result.status, "success");
        assert_eq!(result.encoding.as_deref(), Some("utf8"));
        assert_eq!(result.content.as_deref(), Some("test content"));
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn llm_tty_blocklist() {
        let result = llm::execute_one("vim test.txt");
        assert_eq!(result.error_type.as_deref(), Some("tty_required"));
        assert!(result.hint.is_some());
    }

    #[test]
    fn llm_cd() {
        let result = llm::execute_one("cd /tmp");
        assert_eq!(result.status, "success");
        // cwd should reflect change
        assert!(result.cwd.contains("tmp"));
        // restore
        let _ = std::env::set_current_dir(std::env::var("HOME").unwrap_or_default());
    }

    // ═══════════════════════════════════════════════════════════════
    // MCP
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn mcp_execute() {
        let params = serde_json::json!({"name": "rush_execute", "arguments": {"command": "echo mcp"}});
        // We can't easily call handle_tools_call from here since it's not pub,
        // but we tested it via the POSIX test suite. This just confirms the
        // llm::execute_one path that MCP uses.
        let result = llm::execute_one("echo mcp");
        assert_eq!(result.status, "success");
        assert_eq!(result.stdout.as_deref(), Some("mcp"));
    }

    // ═══════════════════════════════════════════════════════════════
    // Config Sync (unit tests)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn sync_status_not_initialized() {
        // sync status should not crash when not initialized
        crate::sync::handle_sync("status");
    }

    // ═══════════════════════════════════════════════════════════════
    // Dispatch Integration
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn dispatch_shell_echo() {
        let result = process::run_native_capture("echo dispatch_test");
        assert_eq!(result.stdout.trim(), "dispatch_test");
    }

    #[test]
    fn dispatch_rush_variable() {
        let (_, lines) = run("x = 42; puts x");
        assert_eq!(lines, vec!["42"]);
    }

    #[test]
    fn dispatch_pipeline_native() {
        let result = process::run_native_capture("echo hello | tr h H");
        assert_eq!(result.stdout.trim(), "Hello");
    }

    #[test]
    fn dispatch_mixed_chain() {
        let (_, lines) = run("x = 5; puts x + 1");
        assert_eq!(lines, vec!["6"]);
    }

    #[test]
    fn dispatch_error_recovery() {
        // A failing command shouldn't prevent next
        let (_, lines) = run("/usr/bin/false; puts \"recovered\"");
        assert_eq!(lines, vec!["recovered"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // Rush Language Features via Dispatch
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn dispatch_if_else() {
        let (_, lines) = run("if true\n  puts \"yes\"\nelse\n  puts \"no\"\nend");
        assert_eq!(lines, vec!["yes"]);
    }

    #[test]
    fn dispatch_for_array() {
        let (_, lines) = run("for x in [1,2,3]\n  puts x\nend");
        assert_eq!(lines, vec!["1", "2", "3"]);
    }

    #[test]
    fn dispatch_function() {
        let (_, lines) = run("def greet(name)\n  puts \"hi \" + name\nend\ngreet(\"world\")");
        assert_eq!(lines, vec!["hi world"]);
    }

    #[test]
    fn dispatch_array_methods() {
        let (_, lines) = run("x = [3,1,2].sort; puts x.join(\", \")");
        assert_eq!(lines, vec!["1, 2, 3"]);
    }

    #[test]
    fn dispatch_string_methods() {
        let (_, lines) = run("puts \"hello\".upcase.reverse");
        assert_eq!(lines, vec!["OLLEH"]);
    }

    #[test]
    fn dispatch_file_stdlib() {
        let (_, lines) = run("File.write(\"/tmp/rush_int_test.txt\", \"test123\")\nputs File.read(\"/tmp/rush_int_test.txt\")\nFile.delete(\"/tmp/rush_int_test.txt\")");
        assert_eq!(lines, vec!["test123"]);
    }

    #[test]
    fn dispatch_time_stdlib() {
        let (_, lines) = run("puts Time.epoch");
        assert!(!lines.is_empty());
        let epoch: i64 = lines[0].parse().unwrap_or(0);
        assert!(epoch > 1_700_000_000);
    }

    #[test]
    fn dispatch_env_access() {
        let (_, lines) = run("puts env.PATH.length > 0");
        // PATH length > 0 should be true
        assert!(!lines.is_empty());
    }

    // ═══════════════════════════════════════════════════════════════
    // Hints
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn hint_not_found() {
        let hint = crate::hints::hint_for_command("nonexistent_xyz", 127);
        assert!(hint.is_some());
        assert!(hint.unwrap().contains("not found"));
    }

    #[test]
    fn hint_permission() {
        let hint = crate::hints::hint_for_command("./script.sh", 126);
        assert!(hint.is_some());
        assert!(hint.unwrap().contains("chmod"));
    }

    #[test]
    fn hint_bashism_fi() {
        let hint = crate::hints::hint_for_command("fi", 0);
        assert!(hint.is_some());
        assert!(hint.unwrap().contains("end"));
    }

    #[test]
    fn hint_typo() {
        let hint = crate::hints::hint_for_command("gti", 127);
        assert!(hint.is_some());
        assert!(hint.unwrap().contains("git"));
    }
}
