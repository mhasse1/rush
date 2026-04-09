//! Plugin system — execute code blocks in companion language runtimes.
//!
//! `plugin.NAME ... end` sends the block body to a companion binary (`rush-NAME`)
//! over a JSON wire protocol (same as `rush --llm`).
//!
//! Each plugin binary must:
//!   1. Read JSON-quoted commands from stdin (one per line)
//!   2. Write `{status, stdout, stderr, exit_code}` result JSON to stdout
//!   3. Write `{ready: true, ...}` context line after each result
//!
//! Plugin discovery order:
//!   1. `rush-NAME` on PATH
//!   2. `~/.config/rush/plugins/rush-NAME`
//!
//! Sessions are persistent per plugin name — reused across multiple blocks.

use serde_json::Value as JsonValue;
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;

// ── Global plugin session pool ─────────────────────────────────────

static SESSIONS: Mutex<Option<HashMap<String, PluginSession>>> = Mutex::new(None);

fn with_sessions<F, R>(f: F) -> R
where
    F: FnOnce(&mut HashMap<String, PluginSession>) -> R,
{
    let mut guard = SESSIONS.lock().unwrap();
    if guard.is_none() {
        *guard = Some(HashMap::new());
    }
    f(guard.as_mut().unwrap())
}

// ── Public API ─────────────────────────────────────────────────────

/// Execute a block of code via the named plugin.
/// Returns the stdout output or an error message.
pub fn execute(plugin_name: &str, body: &str) -> Result<String, String> {
    if body.trim().is_empty() {
        return Ok(String::new());
    }

    with_sessions(|sessions| {
        // Try existing session
        if let Some(session) = sessions.get_mut(plugin_name) {
            match session.execute(body) {
                Some(result) => return parse_result(result),
                None => {
                    // Session died — remove and reconnect
                    sessions.remove(plugin_name);
                }
            }
        }

        // Find and start plugin
        let binary = find_plugin(plugin_name)
            .ok_or_else(|| format!("plugin '{plugin_name}' not found (install rush-{plugin_name} or place in ~/.config/rush/plugins/)"))?;

        let session = PluginSession::start(&binary, plugin_name)
            .ok_or_else(|| format!("failed to start plugin '{plugin_name}' ({binary})"))?;

        sessions.insert(plugin_name.to_string(), session);

        // Execute on the new session
        let session = sessions.get_mut(plugin_name).unwrap();
        match session.execute(body) {
            Some(result) => parse_result(result),
            None => Err(format!("plugin '{plugin_name}' did not respond")),
        }
    })
}

/// List available plugins (found on PATH and in plugins dir).
pub fn list_available() -> Vec<(String, String)> {
    let mut plugins = Vec::new();

    // Check PATH for rush-* binaries
    if let Ok(path) = std::env::var("PATH") {
        let sep = if cfg!(windows) { ';' } else { ':' };
        for dir in path.split(sep) {
            if let Ok(entries) = std::fs::read_dir(dir) {
                for entry in entries.flatten() {
                    let name = entry.file_name().to_string_lossy().to_string();
                    if name.starts_with("rush-") && name != "rush-cli" {
                        let plugin_name = name.strip_prefix("rush-").unwrap().to_string();
                        let path = entry.path().to_string_lossy().to_string();
                        if !plugins.iter().any(|(n, _): &(String, String)| n == &plugin_name) {
                            plugins.push((plugin_name, path));
                        }
                    }
                }
            }
        }
    }

    // Check ~/.config/rush/plugins/
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_default();
    let plugin_dir = std::path::PathBuf::from(&home)
        .join(".config").join("rush").join("plugins");
    if let Ok(entries) = std::fs::read_dir(&plugin_dir) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if name.starts_with("rush-") {
                let plugin_name = name.strip_prefix("rush-").unwrap().to_string();
                let path = entry.path().to_string_lossy().to_string();
                if !plugins.iter().any(|(n, _): &(String, String)| n == &plugin_name) {
                    plugins.push((plugin_name, path));
                }
            }
        }
    }

    plugins
}

// ── Plugin discovery ───────────────────────────────────────────────

/// Find the binary for a plugin by name.
/// Checks PATH for `rush-NAME`, then `~/.config/rush/plugins/rush-NAME`.
fn find_plugin(name: &str) -> Option<String> {
    let binary_name = format!("rush-{name}");

    // Check PATH
    if let Some(path) = crate::process::which(&binary_name) {
        return Some(path);
    }

    // Check plugins directory
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_default();
    let plugin_path = std::path::PathBuf::from(&home)
        .join(".config").join("rush").join("plugins").join(&binary_name);
    if plugin_path.exists() {
        return Some(plugin_path.to_string_lossy().to_string());
    }

    // Also try with .exe on Windows
    #[cfg(windows)]
    {
        let exe_name = format!("{binary_name}.exe");
        if let Some(path) = crate::process::which(&exe_name) {
            return Some(path);
        }
    }

    None
}

// ── Plugin session ─────────────────────────────────────────────────

/// A persistent session with a companion plugin binary.
/// Same wire protocol as `rush --llm`.
struct PluginSession {
    process: Child,
    #[allow(dead_code)]
    plugin_name: String,
    #[allow(dead_code)]
    binary_path: String,
}

impl PluginSession {
    /// Start a plugin binary and wait for the initial ready line.
    fn start(binary: &str, name: &str) -> Option<Self> {
        let mut child = Command::new(binary)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .ok()?;

        // Read the initial context/ready line
        let stdout = child.stdout.as_mut()?;
        let mut reader = BufReader::new(stdout);
        let mut first_line = String::new();
        reader.read_line(&mut first_line).ok()?;

        if first_line.trim().is_empty() {
            child.kill().ok();
            return None;
        }

        // Verify it's valid JSON with "ready" field
        if let Ok(ctx) = serde_json::from_str::<JsonValue>(&first_line) {
            if ctx.get("ready").and_then(|v| v.as_bool()) == Some(true) {
                eprintln!("[rush-plugin] {name}: session established via {binary}");
                return Some(PluginSession {
                    process: child,
                    plugin_name: name.to_string(),
                    binary_path: binary.to_string(),
                });
            }
        }

        // Not a valid plugin — might just be a regular command
        // Try to use it anyway (some plugins might not send ready)
        eprintln!("[rush-plugin] {name}: started (no ready signal)");
        Some(PluginSession {
            process: child,
            plugin_name: name.to_string(),
            binary_path: binary.to_string(),
        })
    }

    /// Send a command to the plugin and read the result.
    fn execute(&mut self, command: &str) -> Option<JsonValue> {
        let stdin = self.process.stdin.as_mut()?;

        // Send the command as a JSON-quoted string (handles multi-line)
        let json_cmd = serde_json::to_string(command).unwrap_or_else(|_| command.to_string());
        writeln!(stdin, "{json_cmd}").ok()?;
        stdin.flush().ok()?;

        // Read result line
        let stdout = self.process.stdout.as_mut()?;
        let mut reader = BufReader::new(stdout);
        let mut result_line = String::new();
        reader.read_line(&mut result_line).ok()?;

        if result_line.trim().is_empty() {
            return None;
        }

        let result: JsonValue = serde_json::from_str(&result_line).ok()?;

        // Read the next context line (for next command)
        let mut ctx_line = String::new();
        reader.read_line(&mut ctx_line).ok();
        // Context line is informational — we don't need it

        Some(result)
    }
}

impl Drop for PluginSession {
    fn drop(&mut self) {
        self.process.kill().ok();
    }
}

// ── Result parsing ─────────────────────────────────────────────────

/// Parse a JSON result from the plugin wire protocol.
fn parse_result(result: JsonValue) -> Result<String, String> {
    let status = result.get("status").and_then(|s| s.as_str()).unwrap_or("error");
    let stdout = result.get("stdout").and_then(|s| s.as_str()).unwrap_or("");
    let stderr = result.get("stderr").and_then(|s| s.as_str()).unwrap_or("");

    if status == "success" {
        Ok(stdout.to_string())
    } else {
        if !stderr.is_empty() {
            Err(stderr.to_string())
        } else if !stdout.is_empty() {
            // Some commands output errors on stdout
            Err(stdout.to_string())
        } else {
            let code = result.get("exit_code").and_then(|c| c.as_i64()).unwrap_or(-1);
            Err(format!("exit code {code}"))
        }
    }
}

// ── Tests ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn parse_success_result() {
        let result = json!({"status": "success", "stdout": "hello world", "exit_code": 0});
        assert_eq!(parse_result(result).unwrap(), "hello world");
    }

    #[test]
    fn parse_error_result() {
        let result = json!({"status": "error", "stderr": "not found", "exit_code": 1});
        assert!(parse_result(result).is_err());
    }

    #[test]
    fn find_nonexistent_plugin() {
        assert!(find_plugin("nonexistent-plugin-xyz").is_none());
    }

    #[test]
    fn plugin_block_parsed() {
        let input = "plugin.ps\n  Get-Process\nend";
        let nodes = crate::parser::parse(input).unwrap();
        assert_eq!(nodes.len(), 1);
        match &nodes[0] {
            crate::ast::Node::PluginBlock { plugin_name, raw_body } => {
                assert_eq!(plugin_name, "ps");
                assert_eq!(raw_body.trim(), "Get-Process");
            }
            other => panic!("expected PluginBlock, got {other:?}"),
        }
    }

    #[test]
    fn plugin_block_multiline() {
        let input = "plugin.python\n  import json\n  print(json.dumps({\"a\": 1}))\nend";
        let nodes = crate::parser::parse(input).unwrap();
        assert_eq!(nodes.len(), 1);
        match &nodes[0] {
            crate::ast::Node::PluginBlock { plugin_name, raw_body } => {
                assert_eq!(plugin_name, "python");
                assert!(raw_body.contains("import json"));
                assert!(raw_body.contains("print("));
            }
            other => panic!("expected PluginBlock, got {other:?}"),
        }
    }

    #[test]
    fn plugin_block_win32() {
        let input = "plugin.win32\n  [ADSI]\"WinNT://localhost\"\nend";
        let nodes = crate::parser::parse(input).unwrap();
        match &nodes[0] {
            crate::ast::Node::PluginBlock { plugin_name, .. } => {
                assert_eq!(plugin_name, "win32");
            }
            other => panic!("expected PluginBlock, got {other:?}"),
        }
    }
}
