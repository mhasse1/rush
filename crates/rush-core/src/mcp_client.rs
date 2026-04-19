//! MCP (Model Context Protocol) client — outbound JSON-RPC 2.0 over stdio.
//!
//! Lets Rush call external MCP servers as a first-class builtin:
//!
//! ```rush
//! mcp("auditor", "scan_repo", path: ".")
//!   | where severity == "FAIL"
//!   | first 10
//! ```
//!
//! Each server is spawned once per rush session and kept alive; subsequent
//! calls reuse the same process so stateful tools (e.g. auditor's run cache,
//! engram's session) work as expected across a pipeline.
//!
//! Server registry lives at `~/.config/rush/mcp-servers.json`, matching the
//! `mcpServers` shape Claude Desktop uses so users can copy config directly.
//!
//! This is increment 1 of #249: one subprocess per server, basic tool call
//! wiring, JSON-content auto-parsed to a Rush `Value`. Error propagation,
//! resource reads, and ergonomics polish land in later increments.

use crate::value::Value;
use serde_json::{json, Value as JsonValue};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::Duration;

const PROTOCOL_VERSION: &str = "2024-11-05";
const CLIENT_NAME: &str = "rush";
const CLIENT_VERSION: &str = env!("CARGO_PKG_VERSION");

// ── Public API ─────────────────────────────────────────────────────

/// Call a tool on the named MCP server. Spawns the server on first use
/// within this rush session and reuses it on subsequent calls.
///
/// `args` is the tool's argument object; it's forwarded as-is.
/// Returns the tool's result content as a Rush `Value`:
///   - JSON content is parsed (`[{...}, {...}]` → `Value::Array(Value::Hash)`)
///   - Plain text content is returned as `Value::String`
pub fn call_tool(server: &str, tool: &str, args: JsonValue) -> Result<Value, String> {
    with_clients(|clients| {
        // Try existing session first
        if let Some(client) = clients.get_mut(server) {
            match client.call_tool(tool, args.clone()) {
                Ok(val) => return Ok(val),
                Err(e) => {
                    // Session dead or broken — drop and retry with a fresh one
                    eprintln!("[rush-mcp-client] {server}: session error ({e}); reconnecting");
                    clients.remove(server);
                }
            }
        }

        let (registry, trail) = load_registry_with_trail()?;
        let config = registry.into_iter().find(|(k, _)| k == server).map(|(_, v)| v)
            .ok_or_else(|| {
                format!(
                    "mcp: no server '{server}' found. Checked: {}",
                    trail.join("; ")
                )
            })?;

        let mut client = McpClient::spawn(server, &config)?;
        let result = client.call_tool(tool, args)?;
        clients.insert(server.to_string(), client);
        Ok(result)
    })
}

/// List configured MCP servers. Used by diagnostics / tab completion.
pub fn list_servers() -> Vec<String> {
    load_registry()
        .map(|m| m.into_keys().collect())
        .unwrap_or_default()
}

// ── Session pool ───────────────────────────────────────────────────

static CLIENTS: Mutex<Option<HashMap<String, McpClient>>> = Mutex::new(None);

fn with_clients<F, R>(f: F) -> R
where
    F: FnOnce(&mut HashMap<String, McpClient>) -> R,
{
    let mut guard = CLIENTS.lock().unwrap();
    if guard.is_none() {
        *guard = Some(HashMap::new());
    }
    f(guard.as_mut().unwrap())
}

// ── Registry ───────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct McpServerConfig {
    pub command: String,
    pub args: Vec<String>,
    pub env: HashMap<String, String>,
}

/// Candidate registry paths, in priority order (highest first).
/// We union all readable files so legacy configs keep working; on duplicate
/// server names, the higher-priority file wins.
///
/// Order rationale:
/// 1. `~/.config/rush/mcp-servers.json` — rush-native, explicit opt-in.
/// 2. `~/.claude.json` — what `claude mcp add -s user` writes on current CLI.
/// 3. `~/.claude/mcp.json` — legacy Claude Code path; older versions wrote
///    here. Still read so legacy setups work, but current CLI doesn't update
///    it so it may be stale — hence lower priority than ~/.claude.json.
///    4-5. Claude Desktop configs (Linux / macOS) if that's what the user has.
///
/// We only read top-level `mcpServers`; project-scoped entries in
/// `~/.claude.json` (`projects.*.mcpServers`) are ignored — if an entry
/// is project-only and rush needs to call it, copy to the rush-native file.
fn registry_candidates() -> Vec<PathBuf> {
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_default();
    let h = PathBuf::from(home);
    vec![
        h.join(".config").join("rush").join("mcp-servers.json"),
        h.join(".claude.json"),
        h.join(".claude").join("mcp.json"),
        h.join(".config").join("Claude").join("claude_desktop_config.json"),
        h.join("Library").join("Application Support").join("Claude").join("claude_desktop_config.json"),
    ]
}

/// One source file's contribution to the merged registry, plus a trail
/// entry describing what we found there (for error messages).
struct SourceResult {
    servers: HashMap<String, McpServerConfig>,
    trail: String,
}

fn load_source(path: &std::path::Path) -> SourceResult {
    let display = path.display().to_string();

    if !path.exists() {
        return SourceResult {
            servers: HashMap::new(),
            trail: format!("{display} [missing]"),
        };
    }

    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) => {
            return SourceResult {
                servers: HashMap::new(),
                trail: format!("{display} [unreadable: {e}]"),
            };
        }
    };

    let root: JsonValue = match serde_json::from_str(&text) {
        Ok(v) => v,
        Err(e) => {
            return SourceResult {
                servers: HashMap::new(),
                trail: format!("{display} [invalid JSON: {e}]"),
            };
        }
    };

    let Some(obj) = root.get("mcpServers").and_then(|v| v.as_object()) else {
        return SourceResult {
            servers: HashMap::new(),
            trail: format!("{display} [no mcpServers key]"),
        };
    };

    let mut servers = HashMap::new();
    let mut skipped = Vec::new();
    for (name, cfg) in obj {
        let command = match cfg.get("command").and_then(|v| v.as_str()) {
            Some(c) => c.to_string(),
            None => {
                skipped.push(format!("{name} [no command]"));
                continue;
            }
        };
        let args = cfg
            .get("args")
            .and_then(|v| v.as_array())
            .map(|a| {
                a.iter()
                    .filter_map(|x| x.as_str().map(String::from))
                    .collect()
            })
            .unwrap_or_default();
        let env = cfg
            .get("env")
            .and_then(|v| v.as_object())
            .map(|o| {
                o.iter()
                    .filter_map(|(k, v)| v.as_str().map(|s| (k.clone(), s.to_string())))
                    .collect()
            })
            .unwrap_or_default();
        servers.insert(name.clone(), McpServerConfig { command, args, env });
    }

    let mut names: Vec<String> = servers.keys().cloned().collect();
    names.sort();
    let summary = if names.is_empty() {
        "0 servers".to_string()
    } else {
        format!("{} servers: {}", names.len(), names.join(", "))
    };
    let suffix = if skipped.is_empty() {
        String::new()
    } else {
        format!(", skipped: {}", skipped.join(", "))
    };

    SourceResult {
        servers,
        trail: format!("{display} [{summary}{suffix}]"),
    }
}

fn load_registry() -> Result<HashMap<String, McpServerConfig>, String> {
    load_registry_with_trail().map(|(m, _)| m)
}

/// Load every readable candidate file, union the results with priority to
/// earlier files on duplicate keys. Returns `(merged, trail)` where `trail`
/// is a human-readable chain of what was found where.
fn load_registry_with_trail() -> Result<(HashMap<String, McpServerConfig>, Vec<String>), String> {
    let candidates = registry_candidates();
    let mut merged: HashMap<String, McpServerConfig> = HashMap::new();
    let mut trail: Vec<String> = Vec::new();

    for p in candidates {
        let r = load_source(&p);
        trail.push(r.trail);
        for (name, cfg) in r.servers {
            // Higher-priority sources come first, so preserve the first one
            // we see for any given name.
            merged.entry(name).or_insert(cfg);
        }
    }

    if merged.is_empty() {
        return Err(format!(
            "mcp: no server registry found. Checked: {}",
            trail.join("; ")
        ));
    }

    Ok((merged, trail))
}

// ── Client session ─────────────────────────────────────────────────

struct McpClient {
    process: Child,
    next_id: u64,
    server_name: String,
}

impl McpClient {
    fn spawn(name: &str, cfg: &McpServerConfig) -> Result<Self, String> {
        let mut cmd = Command::new(&cfg.command);
        cmd.args(&cfg.args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        for (k, v) in &cfg.env {
            cmd.env(k, v);
        }

        let child = cmd.spawn().map_err(|e| {
            format!(
                "mcp: failed to spawn '{}' for server '{name}': {e}",
                cfg.command
            )
        })?;

        let mut client = McpClient {
            process: child,
            next_id: 1,
            server_name: name.to_string(),
        };

        // MCP handshake: initialize → initialized notification.
        client.handshake()?;
        Ok(client)
    }

    fn handshake(&mut self) -> Result<(), String> {
        let params = json!({
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {
                "name": CLIENT_NAME,
                "version": CLIENT_VERSION,
            }
        });
        let _init_result = self.request("initialize", params)?;
        // Initialized notification (no id, no response expected)
        self.notify("notifications/initialized", json!({}))?;
        Ok(())
    }

    fn call_tool(&mut self, tool: &str, args: JsonValue) -> Result<Value, String> {
        let params = json!({ "name": tool, "arguments": args });
        let result = self.request("tools/call", params)?;

        if result
            .get("isError")
            .and_then(|v| v.as_bool())
            .unwrap_or(false)
        {
            let msg = first_text_content(&result).unwrap_or_else(|| "unknown error".to_string());
            return Err(format!("mcp: {} tool '{tool}' error: {msg}", self.server_name));
        }

        Ok(parse_tool_result(&result))
    }

    fn request(&mut self, method: &str, params: JsonValue) -> Result<JsonValue, String> {
        let id = self.next_id;
        self.next_id += 1;

        let msg = json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": method,
            "params": params,
        });

        let stdin = self
            .process
            .stdin
            .as_mut()
            .ok_or("mcp: child stdin not available")?;
        writeln!(stdin, "{msg}").map_err(|e| format!("mcp: write failed: {e}"))?;
        stdin
            .flush()
            .map_err(|e| format!("mcp: flush failed: {e}"))?;

        let stdout = self
            .process
            .stdout
            .as_mut()
            .ok_or("mcp: child stdout not available")?;
        let mut reader = BufReader::new(stdout);

        // MCP servers may emit notifications/log lines — read until we see
        // a response matching our id.
        loop {
            let mut line = String::new();
            let n = reader
                .read_line(&mut line)
                .map_err(|e| format!("mcp: read failed: {e}"))?;
            if n == 0 {
                return Err("mcp: server closed connection".to_string());
            }
            let trimmed = line.trim();
            if trimmed.is_empty() {
                continue;
            }
            let resp: JsonValue = match serde_json::from_str(trimmed) {
                Ok(v) => v,
                Err(_) => continue, // non-JSON stderr-style noise; skip
            };
            let resp_id = resp.get("id").and_then(|v| v.as_u64());
            if resp_id != Some(id) {
                // Notification or a response to someone else — skip
                continue;
            }
            if let Some(err) = resp.get("error") {
                let code = err.get("code").and_then(|c| c.as_i64()).unwrap_or(-1);
                let msg = err
                    .get("message")
                    .and_then(|m| m.as_str())
                    .unwrap_or("unknown");
                return Err(format!("mcp: JSON-RPC error {code}: {msg}"));
            }
            return Ok(resp.get("result").cloned().unwrap_or(JsonValue::Null));
        }
    }

    fn notify(&mut self, method: &str, params: JsonValue) -> Result<(), String> {
        let msg = json!({
            "jsonrpc": "2.0",
            "method": method,
            "params": params,
        });
        let stdin = self
            .process
            .stdin
            .as_mut()
            .ok_or("mcp: child stdin not available")?;
        writeln!(stdin, "{msg}").map_err(|e| format!("mcp: write failed: {e}"))?;
        stdin
            .flush()
            .map_err(|e| format!("mcp: flush failed: {e}"))?;
        // Give servers a moment to process the initialized notification
        // before the first real request. Many servers do setup here.
        std::thread::sleep(Duration::from_millis(20));
        Ok(())
    }
}

impl Drop for McpClient {
    fn drop(&mut self) {
        // Best-effort shutdown. The MCP spec has a clean "shutdown" request
        // but not all servers honor it cleanly — kill is reliable.
        self.process.kill().ok();
        // Drain stderr so any final server output isn't lost to the void.
        if let Some(mut err) = self.process.stderr.take() {
            let mut buf = String::new();
            err.read_to_string(&mut buf).ok();
            if !buf.trim().is_empty() {
                eprintln!("[rush-mcp-client] {} stderr on shutdown:\n{buf}", self.server_name);
            }
        }
    }
}

// ── Result parsing ─────────────────────────────────────────────────

fn first_text_content(result: &JsonValue) -> Option<String> {
    result
        .get("content")?
        .as_array()?
        .iter()
        .find_map(|c| c.get("text")?.as_str().map(String::from))
}

/// Convert an MCP `tools/call` result into a Rush value.
/// Rules:
///   - If there's a `structuredContent` field (MCP 2025-06 extension), use it.
///   - Else collect `content[*].text` pieces; if a single piece parses as
///     JSON, use that; otherwise return the joined text as a string.
fn parse_tool_result(result: &JsonValue) -> Value {
    if let Some(structured) = result.get("structuredContent") {
        return json_to_value(structured);
    }

    let Some(content) = result.get("content").and_then(|v| v.as_array()) else {
        return Value::Nil;
    };

    // Concatenate text pieces; most servers return one, some return many.
    let mut text_pieces: Vec<String> = Vec::new();
    for c in content {
        if let Some(t) = c.get("text").and_then(|v| v.as_str()) {
            text_pieces.push(t.to_string());
        }
    }

    if text_pieces.is_empty() {
        return Value::Nil;
    }

    let joined = text_pieces.join("\n");
    // Try to auto-parse JSON so tool outputs flow into Rush pipeline ops
    // (| where, | select, ...) without a manual `| from json`.
    if let Ok(parsed) = serde_json::from_str::<JsonValue>(&joined) {
        return json_to_value(&parsed);
    }
    Value::String(joined)
}

/// Convert a Rush `Value` back to JSON for sending as tool arguments.
/// Hash keys lose the leading `:` (symbols) to stay JSON-compatible —
/// same normalization pipeline::as_json already applies.
pub fn value_to_json(val: &Value) -> JsonValue {
    match val {
        Value::Nil => JsonValue::Null,
        Value::Bool(b) => JsonValue::Bool(*b),
        Value::Int(n) => JsonValue::Number((*n).into()),
        Value::Float(f) => serde_json::Number::from_f64(*f)
            .map(JsonValue::Number)
            .unwrap_or(JsonValue::Null),
        Value::String(s) => JsonValue::String(s.clone()),
        Value::Symbol(s) => JsonValue::String(s.trim_start_matches(':').to_string()),
        Value::Array(a) => JsonValue::Array(a.iter().map(value_to_json).collect()),
        Value::Hash(h) => {
            let obj: serde_json::Map<String, JsonValue> = h
                .iter()
                .map(|(k, v)| (k.trim_start_matches(':').to_string(), value_to_json(v)))
                .collect();
            JsonValue::Object(obj)
        }
        Value::Range(start, end, exclusive) => {
            // Serialize as {"start": .., "end": .., "exclusive": ..} so tool
            // authors can see the full shape if they choose to consume it.
            json!({"start": start, "end": end, "exclusive": exclusive})
        }
    }
}

/// Build a JSON object from a Rush hash-like map (for named args).
pub fn value_hash_to_json(h: &HashMap<String, Value>) -> JsonValue {
    let obj: serde_json::Map<String, JsonValue> = h
        .iter()
        .map(|(k, v)| (k.trim_start_matches(':').to_string(), value_to_json(v)))
        .collect();
    JsonValue::Object(obj)
}

fn json_to_value(json: &JsonValue) -> Value {
    match json {
        JsonValue::Null => Value::Nil,
        JsonValue::Bool(b) => Value::Bool(*b),
        JsonValue::Number(n) => {
            if let Some(i) = n.as_i64() {
                Value::Int(i)
            } else if let Some(f) = n.as_f64() {
                Value::Float(f)
            } else {
                Value::String(n.to_string())
            }
        }
        JsonValue::String(s) => Value::String(s.clone()),
        JsonValue::Array(a) => Value::Array(a.iter().map(json_to_value).collect()),
        JsonValue::Object(o) => {
            let mut h = HashMap::new();
            for (k, v) in o {
                h.insert(k.clone(), json_to_value(v));
            }
            Value::Hash(h)
        }
    }
}

// ── Tests ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_to_value_scalars() {
        assert!(matches!(json_to_value(&json!(null)), Value::Nil));
        assert!(matches!(json_to_value(&json!(true)), Value::Bool(true)));
        assert!(matches!(json_to_value(&json!(42)), Value::Int(42)));
        let f = json_to_value(&json!(3.5));
        assert!(matches!(f, Value::Float(x) if (x - 3.5).abs() < 1e-9));
        assert!(matches!(json_to_value(&json!("x")), Value::String(s) if s == "x"));
    }

    #[test]
    fn json_to_value_array_of_hashes() {
        let j = json!([{"a": 1}, {"a": 2}]);
        let v = json_to_value(&j);
        let Value::Array(arr) = v else {
            panic!("expected array");
        };
        assert_eq!(arr.len(), 2);
        let Value::Hash(h) = &arr[0] else {
            panic!("expected hash");
        };
        assert!(matches!(h.get("a"), Some(Value::Int(1))));
    }

    #[test]
    fn parse_tool_result_prefers_structured() {
        let result = json!({
            "content": [{"type": "text", "text": "ignored"}],
            "structuredContent": [{"a": 1}]
        });
        let v = parse_tool_result(&result);
        assert!(matches!(v, Value::Array(_)));
    }

    #[test]
    fn parse_tool_result_auto_parses_json_text() {
        let result = json!({
            "content": [{"type": "text", "text": "[{\"a\":1}]"}]
        });
        let v = parse_tool_result(&result);
        let Value::Array(arr) = v else {
            panic!("expected array");
        };
        assert_eq!(arr.len(), 1);
    }

    #[test]
    fn parse_tool_result_falls_back_to_string() {
        let result = json!({
            "content": [{"type": "text", "text": "plain prose output"}]
        });
        let v = parse_tool_result(&result);
        assert!(matches!(v, Value::String(s) if s == "plain prose output"));
    }

    #[test]
    fn parse_tool_result_empty_content_returns_nil() {
        let result = json!({"content": []});
        assert!(matches!(parse_tool_result(&result), Value::Nil));
    }

    #[test]
    fn first_text_content_extracts_first_text() {
        let result = json!({
            "content": [
                {"type": "image", "data": "..."},
                {"type": "text", "text": "hello"},
                {"type": "text", "text": "world"}
            ]
        });
        assert_eq!(first_text_content(&result).as_deref(), Some("hello"));
    }

    #[test]
    fn load_source_missing_file_has_missing_tag() {
        let p = std::path::Path::new("/tmp/rush_mcp_client_definitely_missing_xyz.json");
        let r = load_source(p);
        assert!(r.servers.is_empty());
        assert!(r.trail.contains("[missing]"), "trail: {}", r.trail);
    }

    #[test]
    fn load_source_parses_mcp_servers_and_summarizes() {
        let tmp = std::env::temp_dir().join(format!(
            "rush_mcp_test_{}.json",
            std::process::id()
        ));
        std::fs::write(
            &tmp,
            r#"{
                "mcpServers": {
                    "alpha": {"command": "true", "args": []},
                    "beta": {"command": "true"}
                }
            }"#,
        )
        .unwrap();
        let r = load_source(&tmp);
        assert_eq!(r.servers.len(), 2);
        assert!(r.trail.contains("2 servers: alpha, beta"), "trail: {}", r.trail);
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn load_source_reports_invalid_json() {
        let tmp = std::env::temp_dir().join(format!(
            "rush_mcp_invalid_{}.json",
            std::process::id()
        ));
        std::fs::write(&tmp, "{ not json").unwrap();
        let r = load_source(&tmp);
        assert!(r.servers.is_empty());
        assert!(r.trail.contains("[invalid JSON"), "trail: {}", r.trail);
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn load_source_reports_missing_mcpservers_key() {
        let tmp = std::env::temp_dir().join(format!(
            "rush_mcp_nokey_{}.json",
            std::process::id()
        ));
        std::fs::write(&tmp, r#"{"other": 42}"#).unwrap();
        let r = load_source(&tmp);
        assert!(r.servers.is_empty());
        assert!(r.trail.contains("[no mcpServers key]"), "trail: {}", r.trail);
        std::fs::remove_file(&tmp).ok();
    }
}
