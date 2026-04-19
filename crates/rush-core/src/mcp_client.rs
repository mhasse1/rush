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

        let config = load_registry()?
            .remove(server)
            .ok_or_else(|| format!("mcp: no server '{server}' in ~/.config/rush/mcp-servers.json"))?;

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

/// Candidate registry paths, in priority order. First one that exists wins.
///
/// Rush-specific file lets users register extra servers just for rush;
/// Claude Code and Claude Desktop configs make rush work out-of-the-box
/// if either is already set up. They share the `mcpServers` shape.
fn registry_candidates() -> Vec<PathBuf> {
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_default();
    let h = PathBuf::from(home);
    vec![
        h.join(".config").join("rush").join("mcp-servers.json"),
        h.join(".claude").join("mcp.json"),
        h.join(".config").join("Claude").join("claude_desktop_config.json"),
        h.join("Library").join("Application Support").join("Claude").join("claude_desktop_config.json"),
    ]
}

fn load_registry() -> Result<HashMap<String, McpServerConfig>, String> {
    let candidates = registry_candidates();
    let mut attempts = Vec::new();
    let (path, text) = candidates
        .into_iter()
        .find_map(|p| match std::fs::read_to_string(&p) {
            Ok(text) => Some((p, text)),
            Err(_) => {
                attempts.push(p.display().to_string());
                None
            }
        })
        .ok_or_else(|| {
            format!(
                "mcp: no server registry found. Tried: {}",
                attempts.join(", ")
            )
        })?;
    let root: JsonValue = serde_json::from_str(&text)
        .map_err(|e| format!("mcp: {} is not valid JSON: {e}", path.display()))?;
    let servers = root
        .get("mcpServers")
        .and_then(|v| v.as_object())
        .ok_or_else(|| "mcp: registry missing 'mcpServers' object".to_string())?;

    let mut out = HashMap::new();
    for (name, cfg) in servers {
        let command = cfg
            .get("command")
            .and_then(|v| v.as_str())
            .ok_or_else(|| format!("mcp: server '{name}' missing 'command' field"))?
            .to_string();
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
        out.insert(name.clone(), McpServerConfig { command, args, env });
    }
    Ok(out)
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
}
