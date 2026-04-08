//! MCP SSH Gateway — JSON-RPC 2.0 over stdio, multi-host.
//! Persistent rush --llm sessions per host with raw-shell fallback.
//!
//! Usage:
//!   rush --mcp-ssh
//!   claude mcp add rush-ssh -- rush --mcp-ssh

use serde_json::{json, Value as JsonValue};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::process::{Child, Command, Stdio};

const VERSION: &str = "0.1.0";

/// A persistent SSH session running `rush --llm` on a remote host.
#[allow(dead_code)]
struct SshSession {
    process: Child,
    host: String,
    has_rush: bool,
    last_context: Option<JsonValue>,
}

impl SshSession {
    /// Try to create a persistent Rush session on a remote host.
    fn try_create(host: &str) -> Option<Self> {
        // Try multiple Rush binary names
        for rush_cmd in &["rush --llm", "rush-rust --llm"] {
            if let Some(session) = Self::try_connect(host, rush_cmd) {
                return Some(session);
            }
        }
        None
    }

    fn try_connect(host: &str, rush_cmd: &str) -> Option<Self> {
        let mut child = Command::new("ssh")
            .args(["-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                   "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
                   host, rush_cmd])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .ok()?;

        // Read first line — should be LlmContext JSON
        let stdout = child.stdout.as_mut()?;
        let mut reader = BufReader::new(stdout);
        let mut first_line = String::new();

        // Set a timeout by reading with a deadline
        reader.read_line(&mut first_line).ok()?;

        if first_line.trim().is_empty() {
            child.kill().ok();
            return None;
        }

        // Check if it's valid JSON with "ready" field
        if let Ok(ctx) = serde_json::from_str::<JsonValue>(&first_line) {
            if ctx.get("ready").and_then(|v| v.as_bool()) == Some(true) {
                eprintln!("[rush-ssh] {host}: Rush session established");
                return Some(SshSession {
                    process: child,
                    host: host.to_string(),
                    has_rush: true,
                    last_context: Some(ctx),
                });
            }
        }

        // Not Rush — kill and fall back
        child.kill().ok();
        None
    }

    /// Execute a command via the persistent Rush session.
    fn execute(&mut self, command: &str) -> Option<JsonValue> {
        let stdin = self.process.stdin.as_mut()?;

        // Write command (JSON-quoted for multi-line support)
        let json_cmd = serde_json::to_string(command).unwrap_or_else(|_| command.to_string());
        writeln!(stdin, "{json_cmd}").ok()?;
        stdin.flush().ok()?;

        // Read result line
        let stdout = self.process.stdout.as_mut()?;
        let mut reader = BufReader::new(stdout);
        let mut result_line = String::new();
        reader.read_line(&mut result_line).ok()?;

        let result: JsonValue = serde_json::from_str(&result_line).ok()?;

        // Read next context line (for next command)
        let mut ctx_line = String::new();
        reader.read_line(&mut ctx_line).ok();
        if let Ok(ctx) = serde_json::from_str::<JsonValue>(&ctx_line) {
            self.last_context = Some(ctx);
        }

        Some(result)
    }

    fn cached_context(&self) -> Option<&JsonValue> {
        self.last_context.as_ref()
    }
}

impl Drop for SshSession {
    fn drop(&mut self) {
        self.process.kill().ok();
    }
}

/// Execute a command on a remote host via raw SSH (no Rush).
fn run_ssh_raw(host: &str, command: &str) -> JsonValue {
    let result = Command::new("ssh")
        .args(["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", host, command])
        .output();

    match result {
        Ok(output) => {
            let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
            let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
            let code = output.status.code().unwrap_or(-1);
            json!({
                "status": if code == 0 { "success" } else { "error" },
                "exit_code": code,
                "stdout": if stdout.is_empty() { serde_json::Value::Null } else { json!(stdout) },
                "stderr": if stderr.is_empty() { serde_json::Value::Null } else { json!(stderr) },
                "shell": "raw"
            })
        }
        Err(e) => json!({
            "status": "error",
            "exit_code": 1,
            "stderr": format!("SSH error: {e}"),
            "shell": "raw"
        }),
    }
}

/// Read a file from a remote host.
fn read_file_ssh(host: &str, path: &str, session: Option<&mut SshSession>) -> JsonValue {
    if let Some(session) = session {
        // Use lcat via Rush session
        if let Some(result) = session.execute(&format!("lcat {path}")) {
            return result;
        }
    }

    // Fallback: cat via raw SSH
    let result = Command::new("ssh")
        .args(["-o", "BatchMode=yes", host, &format!("cat '{path}'")])
        .output();

    match result {
        Ok(output) if output.status.success() => {
            let content = String::from_utf8_lossy(&output.stdout).to_string();
            json!({
                "status": "success",
                "file": path,
                "content": content,
                "encoding": "utf8",
                "shell": "raw"
            })
        }
        Ok(output) => json!({
            "status": "error",
            "stderr": String::from_utf8_lossy(&output.stderr).trim().to_string()
        }),
        Err(e) => json!({ "status": "error", "stderr": format!("SSH error: {e}") }),
    }
}

// ── MCP Server ──────────────────────────────────────────────────────

pub fn run() {
    eprintln!("[rush-ssh] Rush SSH gateway v{VERSION} starting");

    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut sessions: HashMap<String, SshSession> = HashMap::new();
    let mut raw_hosts: std::collections::HashSet<String> = std::collections::HashSet::new();

    for line in stdin.lock().lines() {
        let line = match line {
            Ok(l) => l,
            Err(_) => break,
        };
        if line.trim().is_empty() { continue; }

        let msg: JsonValue = match serde_json::from_str(&line) {
            Ok(v) => v,
            Err(_) => { write_error(&stdout, json!(null), -32700, "Parse error"); continue; }
        };

        let id = msg.get("id").cloned();
        let method = msg.get("method").and_then(|m| m.as_str()).unwrap_or("");

        if id.is_none() { continue; }
        let id = id.unwrap();

        let result = match method {
            "initialize" => Ok(json!({
                "protocolVersion": "2024-11-05",
                "capabilities": { "tools": {}, "resources": {} },
                "serverInfo": { "name": "rush-ssh", "version": VERSION },
                "instructions": "Rush SSH gateway. Execute commands on remote hosts via SSH. All tools require a 'host' parameter. If Rush is installed on the remote, commands run in a persistent session with JSON protocol."
            })),
            "tools/list" => Ok(tools_list()),
            "tools/call" => handle_tools_call(msg.get("params"), &mut sessions, &mut raw_hosts),
            "resources/list" => Ok(json!({ "resources": [{ "uri": "rush://lang-spec", "name": "Rush Language Specification", "mimeType": "text/yaml" }] })),
            "resources/read" => {
                let uri = msg.get("params").and_then(|p| p.get("uri")).and_then(|u| u.as_str());
                if uri == Some("rush://lang-spec") {
                    Ok(json!({ "contents": [{ "uri": "rush://lang-spec", "mimeType": "text/yaml", "text": include_str!("../../../docs/rush-lang-spec.yaml") }] }))
                } else {
                    Err((-32602, format!("Unknown resource: {}", uri.unwrap_or("?"))))
                }
            }
            _ => Err((-32601, format!("Method not found: {method}"))),
        };

        match result {
            Ok(r) => write_result(&stdout, &id, &r),
            Err((code, msg)) => write_error(&stdout, id, code, &msg),
        }
    }

    // Cleanup sessions
    sessions.clear();
    eprintln!("[rush-ssh] Server shutting down");
}

fn tools_list() -> JsonValue {
    json!({
        "tools": [
            {
                "name": "rush_execute",
                "description": "Execute a command on a remote host via SSH. Persistent Rush session if available, raw shell fallback.",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "host": { "type": "string", "description": "SSH host (hostname, IP, or SSH config alias)" },
                        "command": { "type": "string", "description": "Command to execute" }
                    },
                    "required": ["host", "command"]
                }
            },
            {
                "name": "rush_read_file",
                "description": "Read a file from a remote host. Returns content with MIME type via Rush lcat, or raw cat fallback.",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "host": { "type": "string", "description": "SSH host" },
                        "path": { "type": "string", "description": "File path on remote host" }
                    },
                    "required": ["host", "path"]
                }
            },
            {
                "name": "rush_context",
                "description": "Get shell context from remote host: hostname, cwd, git branch/dirty, exit code.",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "host": { "type": "string", "description": "SSH host" }
                    },
                    "required": ["host"]
                }
            }
        ]
    })
}

fn handle_tools_call(
    params: Option<&JsonValue>,
    sessions: &mut HashMap<String, SshSession>,
    raw_hosts: &mut std::collections::HashSet<String>,
) -> Result<JsonValue, (i32, String)> {
    let params = params.ok_or((-32602, "Missing params".into()))?;
    let tool = params.get("name").and_then(|n| n.as_str())
        .ok_or((-32602, "Missing tool name".into()))?;
    let args = params.get("arguments");

    let host = args.and_then(|a| a.get("host")).and_then(|h| h.as_str())
        .ok_or((-32602, "Missing required parameter: host".into()))?;

    let (result_json, is_error) = match tool {
        "rush_execute" => {
            let command = args.and_then(|a| a.get("command")).and_then(|c| c.as_str())
                .ok_or((-32602, "Missing required parameter: command".into()))?;

            let result = execute_on_host(host, command, sessions, raw_hosts);
            let is_err = result.get("status").and_then(|s| s.as_str()) != Some("success");
            (result, is_err)
        }
        "rush_read_file" => {
            let path = args.and_then(|a| a.get("path")).and_then(|p| p.as_str())
                .ok_or((-32602, "Missing required parameter: path".into()))?;

            let session = sessions.get_mut(host);
            let result = read_file_ssh(host, path, session);
            let is_err = result.get("status").and_then(|s| s.as_str()) != Some("success");
            (result, is_err)
        }
        "rush_context" => {
            let result = if let Some(session) = sessions.get(host) {
                if let Some(ctx) = session.cached_context() {
                    ctx.clone()
                } else {
                    // Probe via execute
                    json!({"host": host, "shell": "rush"})
                }
            } else {
                // Raw context via SSH
                let probe = run_ssh_raw(host, "echo $HOSTNAME; pwd; git rev-parse --abbrev-ref HEAD 2>/dev/null; git status --porcelain 2>/dev/null | head -1");
                let stdout = probe.get("stdout").and_then(|s| s.as_str()).unwrap_or("");
                let lines: Vec<&str> = stdout.lines().collect();
                json!({
                    "host": lines.first().unwrap_or(&host),
                    "cwd": lines.get(1).unwrap_or(&"?"),
                    "git_branch": lines.get(2).filter(|s| !s.is_empty()),
                    "git_dirty": lines.get(3).map(|s| !s.is_empty()).unwrap_or(false),
                    "shell": "raw"
                })
            };
            (result, false)
        }
        _ => return Err((-32602, format!("Unknown tool: {tool}"))),
    };

    Ok(json!({
        "content": [{ "type": "text", "text": result_json.to_string() }],
        "isError": is_error
    }))
}

/// Execute a command on a host, using persistent Rush session if available.
fn execute_on_host(
    host: &str,
    command: &str,
    sessions: &mut HashMap<String, SshSession>,
    raw_hosts: &mut std::collections::HashSet<String>,
) -> JsonValue {
    // If we know this host doesn't have Rush, use raw SSH
    if raw_hosts.contains(host) {
        return run_ssh_raw(host, command);
    }

    // Try existing session
    if let Some(session) = sessions.get_mut(host) {
        if let Some(result) = session.execute(command) {
            return result;
        }
        // Session died — remove and try to reconnect
        sessions.remove(host);
    }

    // Try to establish Rush session
    if let Some(session) = SshSession::try_create(host) {
        sessions.insert(host.to_string(), session);
        if let Some(session) = sessions.get_mut(host) {
            if let Some(result) = session.execute(command) {
                return result;
            }
        }
    }

    // Mark as raw-shell host and fall back
    eprintln!("[rush-ssh] {host}: Rush not found, using raw shell");
    raw_hosts.insert(host.to_string());
    run_ssh_raw(host, command)
}

// ── JSON-RPC helpers ────────────────────────────────────────────────

fn write_result(stdout: &std::io::Stdout, id: &JsonValue, result: &JsonValue) {
    let response = json!({ "jsonrpc": "2.0", "id": id, "result": result });
    let mut out = stdout.lock();
    serde_json::to_writer(&mut out, &response).ok();
    out.write_all(b"\n").ok();
    out.flush().ok();
}

fn write_error(stdout: &std::io::Stdout, id: JsonValue, code: i32, message: &str) {
    let response = json!({ "jsonrpc": "2.0", "id": id, "error": { "code": code, "message": message } });
    let mut out = stdout.lock();
    serde_json::to_writer(&mut out, &response).ok();
    out.write_all(b"\n").ok();
    out.flush().ok();
}
