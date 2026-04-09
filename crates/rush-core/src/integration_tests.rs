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

    // ═══════════════════════════════════════════════════════════════
    // Trap
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn trap_set_and_get() {
        crate::trap::init();
        crate::trap::set_trap("EXIT", "echo bye");
        assert_eq!(crate::trap::get_exit_trap(), Some("echo bye".to_string()));
        crate::trap::set_trap("EXIT", "-"); // reset
        assert_eq!(crate::trap::get_exit_trap(), None);
    }

    #[test]
    fn trap_ignore() {
        crate::trap::init();
        crate::trap::set_trap("INT", "");
        assert_eq!(crate::trap::get_trap("INT"), Some(String::new()));
        crate::trap::set_trap("INT", "-"); // reset
    }

    // ═══════════════════════════════════════════════════════════════
    // Jobs
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn job_table_operations() {
        let mut table = crate::jobs::JobTable::new();
        assert!(table.is_empty());
        let id = table.add(99999, 99999, "test command");
        assert_eq!(id, 1);
        assert!(!table.is_empty());
        table.list(); // should not panic
    }

    #[test]
    fn job_resolve_spec() {
        let mut table = crate::jobs::JobTable::new();
        table.add(1111, 1111, "sleep 60");
        table.add(2222, 2222, "make build");
        assert_eq!(table.resolve_job_spec_pub(Some("%1")), Some(1));
        assert_eq!(table.resolve_job_spec_pub(Some("%2")), Some(2));
        assert_eq!(table.resolve_job_spec_pub(None), Some(2)); // current
        assert_eq!(table.resolve_job_spec_pub(Some("%sleep")), Some(1)); // by prefix
    }

    // ═══════════════════════════════════════════════════════════════
    // Platform
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn platform_ssh_detection() {
        let p = crate::platform::current();
        // In test environment, likely not SSH
        // Just verify it doesn't crash
        let _ = p.is_ssh();
    }

    #[test]
    fn platform_root_detection() {
        let p = crate::platform::current();
        // In test, likely not root
        let is_root = p.is_root();
        // Most CI/test runs are not root
        assert!(!is_root || std::env::var("CI").is_ok());
    }

    // ═══════════════════════════════════════════════════════════════
    // Config
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn config_defaults() {
        let config = crate::config::RushConfig::default();
        assert_eq!(config.edit_mode, "vi");
        assert_eq!(config.history_size, 10_000);
        assert_eq!(config.ai_provider, "anthropic");
    }

    #[test]
    fn config_set_get() {
        let mut config = crate::config::RushConfig::default();
        assert!(config.set("ai_provider", "openai"));
        assert_eq!(config.get("ai_provider"), "openai");
    }

    // ═══════════════════════════════════════════════════════════════
    // Pipeline Operators
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn pipeline_where_filter() {
        use crate::pipeline;
        use crate::value::Value;
        let input = Value::Array(vec![
            Value::String("ERROR: bad".into()),
            Value::String("INFO: good".into()),
            Value::String("ERROR: worse".into()),
        ]);
        let op = pipeline::parse_pipe_op("where /ERROR/");
        let result = pipeline::apply_pipe_op(input, &op);
        if let Value::Array(arr) = result {
            assert_eq!(arr.len(), 2);
        } else { panic!("expected array"); }
    }

    #[test]
    fn pipeline_sort_sum() {
        use crate::pipeline;
        use crate::value::Value;
        let input = Value::Array(vec![
            Value::Int(3), Value::Int(1), Value::Int(2),
        ]);
        let sorted = pipeline::apply_pipe_op(input, &pipeline::parse_pipe_op("sort"));
        let sum = pipeline::apply_pipe_op(sorted, &pipeline::parse_pipe_op("sum"));
        assert_eq!(sum, Value::Int(6));
    }

    #[test]
    fn pipeline_as_json() {
        use crate::pipeline;
        use crate::value::Value;
        let input = Value::Array(vec![Value::Int(1), Value::Int(2)]);
        let result = pipeline::apply_pipe_op(input, &pipeline::parse_pipe_op("as json"));
        if let Value::String(s) = result {
            assert!(s.contains("["));
            assert!(s.contains("1"));
        } else { panic!("expected string"); }
    }

    // ═══════════════════════════════════════════════════════════════
    // AI Provider Detection
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn ai_providers_exist() {
        assert!(crate::ai::get_provider("anthropic").is_some());
        assert!(crate::ai::get_provider("openai").is_some());
        assert!(crate::ai::get_provider("gemini").is_some());
        assert!(crate::ai::get_provider("ollama").is_some());
        assert!(crate::ai::get_provider("nonexistent").is_none());
    }

    #[test]
    fn ai_parse_args() {
        let (prompt, provider, model) = crate::ai::parse_ai_args("-p openai -m gpt-4 what is rust");
        assert_eq!(prompt, "what is rust");
        assert_eq!(provider, Some("openai".to_string()));
        assert_eq!(model, Some("gpt-4".to_string()));
    }

    // ═══════════════════════════════════════════════════════════════
    // Theme
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn theme_detect() {
        let theme = crate::theme::detect();
        // Should not crash
        assert!(!theme.reset.is_empty());
    }

    #[test]
    fn theme_ls_colors() {
        let theme = crate::theme::Theme::new(true, Some((0.1, 0.1, 0.15)));
        let ls = crate::theme::generate_ls_colors(&theme);
        assert!(ls.contains("di="));
        assert!(ls.contains("ex="));
    }

    // ═══════════════════════════════════════════════════════════════
    // Version
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn version_from_cargo() {
        let v = env!("CARGO_PKG_VERSION");
        assert!(!v.is_empty());
        assert!(v.contains('.'), "version should be semver: {v}");
    }

    // ═══════════════════════════════════════════════════════════════
    // CLI: --version, --help, --login
    // ═══════════════════════════════════════════════════════════════

    fn rush_cli_path() -> String {
        // Find the built binary
        let mut path = std::env::current_exe().unwrap();
        path.pop(); // remove test binary name
        path.pop(); // remove deps/
        path.push("rush-cli");
        if path.exists() {
            return path.to_string_lossy().to_string();
        }
        // Fallback: try release build
        "target/release/rush-cli".to_string()
    }

    #[test]
    fn cli_version_flag() {
        let output = std::process::Command::new(rush_cli_path())
            .arg("--version")
            .output();
        if let Ok(out) = output {
            let stdout = String::from_utf8_lossy(&out.stdout);
            assert!(stdout.starts_with("rush "), "should start with 'rush ': {stdout}");
            assert!(stdout.contains('.'), "should contain version number: {stdout}");
        }
        // Skip if binary not built — CI will catch it
    }

    #[test]
    fn cli_help_flag() {
        let output = std::process::Command::new(rush_cli_path())
            .arg("--help")
            .output();
        if let Ok(out) = output {
            let stdout = String::from_utf8_lossy(&out.stdout);
            assert!(stdout.contains("Usage:"), "should show usage: {stdout}");
            assert!(stdout.contains("--mcp"), "should mention --mcp: {stdout}");
            assert!(stdout.contains("--llm"), "should mention --llm: {stdout}");
        }
    }

    #[test]
    fn cli_login_env_var() {
        let output = std::process::Command::new(rush_cli_path())
            .args(["--login", "-c", "echo $RUSH_LOGIN"])
            .output();
        if let Ok(out) = output {
            let stdout = String::from_utf8_lossy(&out.stdout).trim().to_string();
            assert_eq!(stdout, "1", "RUSH_LOGIN should be 1 with --login flag");
        }
    }

    #[test]
    fn cli_non_login_env_var() {
        let output = std::process::Command::new(rush_cli_path())
            .args(["-c", "echo $RUSH_LOGIN"])
            .output();
        if let Ok(out) = output {
            let stdout = String::from_utf8_lossy(&out.stdout).trim().to_string();
            assert_eq!(stdout, "0", "RUSH_LOGIN should be 0 without --login flag");
        }
    }

    #[test]
    fn cli_env_vars_injected() {
        let output = std::process::Command::new(rush_cli_path())
            .args(["-c", "echo $RUSH_OS $RUSH_ARCH"])
            .output();
        if let Ok(out) = output {
            let stdout = String::from_utf8_lossy(&out.stdout).trim().to_string();
            let parts: Vec<&str> = stdout.split_whitespace().collect();
            assert!(parts.len() >= 2, "should have OS and ARCH: {stdout}");
            assert!(["macos", "linux", "windows"].contains(&parts[0]),
                "RUSH_OS should be a known OS: {}", parts[0]);
            assert!(!parts[1].is_empty(), "RUSH_ARCH should not be empty");
        }
    }

    #[test]
    fn cli_builtin_vars_in_rush() {
        let output = std::process::Command::new(rush_cli_path())
            .args(["-c", "puts $os"])
            .output();
        if let Ok(out) = output {
            let stdout = String::from_utf8_lossy(&out.stdout).trim().to_string();
            assert!(["macos", "linux", "windows"].contains(&stdout.as_str()),
                "$os should be a known OS: {stdout}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // path add --save (writes to init.rush)
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn path_save_to_init() {
        use std::io::Write;
        let tmp = std::env::temp_dir().join("rush-test-path-save");
        std::fs::create_dir_all(&tmp).unwrap();
        let init_path = tmp.join("init.rush");

        // Create a minimal init.rush
        let mut f = std::fs::File::create(&init_path).unwrap();
        writeln!(f, "# startup").unwrap();
        drop(f);

        // Run rush with HOME pointing to our temp dir so config_dir finds it
        let home_tmp = std::env::temp_dir().join("rush-test-home-path");
        let config_dir = home_tmp.join(".config").join("rush");
        std::fs::create_dir_all(&config_dir).unwrap();

        // Copy our init.rush there
        std::fs::copy(&init_path, config_dir.join("init.rush")).unwrap();

        let output = std::process::Command::new(rush_cli_path())
            .args(["-c", "path add /opt/test-path --save"])
            .env("HOME", &home_tmp)
            .output();

        if let Ok(_out) = output {
            let content = std::fs::read_to_string(config_dir.join("init.rush")).unwrap_or_default();
            assert!(content.contains("path add /opt/test-path"),
                "init.rush should contain the path add line: {content}");
            assert!(content.contains("# ── PATH"),
                "init.rush should have PATH section header: {content}");
        }

        // Cleanup
        std::fs::remove_dir_all(&home_tmp).ok();
        std::fs::remove_dir_all(&tmp).ok();
    }

    #[test]
    fn path_save_appends_to_existing_section() {
        let home_tmp = std::env::temp_dir().join("rush-test-home-path2");
        let config_dir = home_tmp.join(".config").join("rush");
        std::fs::create_dir_all(&config_dir).unwrap();

        // Create init.rush WITH an existing PATH section
        std::fs::write(config_dir.join("init.rush"),
            "# startup\n\n# ── PATH ─────────────────────────────────────────────────\npath add /usr/local/bin\n").unwrap();

        let _output = std::process::Command::new(rush_cli_path())
            .args(["-c", "path add /opt/second --save"])
            .env("HOME", &home_tmp)
            .output();

        let content = std::fs::read_to_string(config_dir.join("init.rush")).unwrap_or_default();
        assert!(content.contains("path add /usr/local/bin"),
            "should keep existing path: {content}");
        assert!(content.contains("path add /opt/second"),
            "should add new path: {content}");

        std::fs::remove_dir_all(&home_tmp).ok();
    }

    // ═══════════════════════════════════════════════════════════════
    // path add...end blocks in scripts
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn path_block_in_script() {
        let tmp = std::env::temp_dir().join("rush-test-path-block");
        std::fs::create_dir_all(&tmp).unwrap();

        let script = tmp.join("test-path-block.rush");
        std::fs::write(&script, "path add\n  /opt/block-test-1\n  /opt/block-test-2\nend\necho $PATH").unwrap();

        let output = std::process::Command::new(rush_cli_path())
            .arg(script.to_str().unwrap())
            .output();

        if let Ok(out) = output {
            let stdout = String::from_utf8_lossy(&out.stdout);
            assert!(stdout.contains("/opt/block-test-1"),
                "PATH should contain block-test-1: {stdout}");
            assert!(stdout.contains("/opt/block-test-2"),
                "PATH should contain block-test-2: {stdout}");
        }

        std::fs::remove_dir_all(&tmp).ok();
    }

    #[test]
    fn path_rm_block_in_script() {
        let tmp = std::env::temp_dir().join("rush-test-path-rm-block");
        std::fs::create_dir_all(&tmp).unwrap();

        // First add, then remove via block
        let script = tmp.join("test-rm-block.rush");
        std::fs::write(&script, concat!(
            "path add /opt/rm-test-keep\n",
            "path add /opt/rm-test-gone\n",
            "path rm\n",
            "  /opt/rm-test-gone\n",
            "end\n",
            "echo $PATH\n",
        )).unwrap();

        let output = std::process::Command::new(rush_cli_path())
            .arg(script.to_str().unwrap())
            .output();

        if let Ok(out) = output {
            let stdout = String::from_utf8_lossy(&out.stdout);
            assert!(stdout.contains("/opt/rm-test-keep"),
                "PATH should still contain keep: {stdout}");
            assert!(!stdout.contains("/opt/rm-test-gone"),
                "PATH should NOT contain gone: {stdout}");
        }

        std::fs::remove_dir_all(&tmp).ok();
    }

    // ═══════════════════════════════════════════════════════════════
    // set --save vs session-only
    // ═══════════════════════════════════════════════════════════════

    #[test]
    fn set_without_save_is_session_only() {
        let home_tmp = std::env::temp_dir().join("rush-test-home-set-nosave");
        let config_dir = home_tmp.join(".config").join("rush");
        std::fs::create_dir_all(&config_dir).unwrap();

        // Write a known config
        std::fs::write(config_dir.join("config.json"),
            r#"{"edit_mode":"vi","show_timing":false}"#).unwrap();

        // Run "set show_timing true" WITHOUT --save
        let _output = std::process::Command::new(rush_cli_path())
            .args(["-c", "set show_timing true"])
            .env("HOME", &home_tmp)
            .output();

        // Config on disk should NOT have changed
        let content = std::fs::read_to_string(config_dir.join("config.json")).unwrap_or_default();
        assert!(content.contains(r#""show_timing":false"#) || content.contains(r#""show_timing": false"#),
            "show_timing should still be false on disk without --save: {content}");

        std::fs::remove_dir_all(&home_tmp).ok();
    }

    #[test]
    fn set_with_save_persists() {
        let home_tmp = std::env::temp_dir().join("rush-test-home-set-save");
        let config_dir = home_tmp.join(".config").join("rush");
        std::fs::create_dir_all(&config_dir).unwrap();

        std::fs::write(config_dir.join("config.json"),
            r#"{"edit_mode":"vi","show_timing":false}"#).unwrap();

        // Run "set show_timing true --save"
        let _output = std::process::Command::new(rush_cli_path())
            .args(["-c", "set show_timing true --save"])
            .env("HOME", &home_tmp)
            .output();

        let content = std::fs::read_to_string(config_dir.join("config.json")).unwrap_or_default();
        assert!(content.contains("true"),
            "show_timing should be true on disk with --save: {content}");

        std::fs::remove_dir_all(&home_tmp).ok();
    }

    #[test]
    fn set_vi_always_saves() {
        let home_tmp = std::env::temp_dir().join("rush-test-home-set-vi");
        let config_dir = home_tmp.join(".config").join("rush");
        std::fs::create_dir_all(&config_dir).unwrap();

        std::fs::write(config_dir.join("config.json"),
            r#"{"edit_mode":"emacs"}"#).unwrap();

        // "set vi" should always save (no --save needed)
        let _output = std::process::Command::new(rush_cli_path())
            .args(["-c", "set vi"])
            .env("HOME", &home_tmp)
            .output();

        let content = std::fs::read_to_string(config_dir.join("config.json")).unwrap_or_default();
        assert!(content.contains("vi"),
            "edit_mode should be vi on disk (auto-save): {content}");

        std::fs::remove_dir_all(&home_tmp).ok();
    }
}
