//! MCP (Model Context Protocol) server — JSON-RPC 2.0 over stdio.
//! Hand-rolled protocol, no framework dependency.
//!
//! Usage:
//!   rush --mcp                              # start MCP server
//!   claude mcp add rush-local -- rush --mcp # register with Claude Code

use serde_json::{json, Value as JsonValue};
use std::io::{BufRead, Write};

use crate::llm;

const VERSION: &str = "0.1.0";

const INSTRUCTIONS: &str = "\
Rush is a Unix-style shell with clean, intent-driven syntax. \
Supports variables (x = 42), string interpolation (\"hello #{name}\"), \
arrays ([1,2,3]), hashes ({a: 1}), control flow (if/unless/while/for-in), \
method chaining (\"hello\".upcase), pipeline operators (| where/select/sort/as json), \
and a File/Dir/Time stdlib. Also runs standard Unix commands (ls, grep, find, etc.). \
Read the rush://lang-spec resource for the full language specification.";

// Embed the lang spec at compile time
const LANG_SPEC: &str = include_str!("../../../docs/rush-lang-spec.yaml");

/// Run the MCP server on stdin/stdout.
pub fn run() {
    // Machine-friendly env
    unsafe {
        std::env::set_var("NO_COLOR", "1");
        std::env::set_var("CI", "true");
        std::env::set_var("GIT_TERMINAL_PROMPT", "0");
    }

    eprintln!("[rush-mcp] Rush MCP server v{VERSION} starting");

    let stdin = std::io::stdin();
    let stdout = std::io::stdout();

    for line in stdin.lock().lines() {
        let line = match line {
            Ok(l) => l,
            Err(_) => break,
        };

        if line.trim().is_empty() {
            continue;
        }

        let msg: JsonValue = match serde_json::from_str(&line) {
            Ok(v) => v,
            Err(_) => {
                write_error(&stdout, JsonValue::Null, -32700, "Parse error");
                continue;
            }
        };

        let id = msg.get("id").cloned();
        let method = msg.get("method").and_then(|m| m.as_str()).unwrap_or("");

        // Notifications (no id) — swallow silently
        if id.is_none() {
            continue;
        }
        let id = id.unwrap();

        let result = match method {
            "initialize" => handle_initialize(),
            "tools/list" => handle_tools_list(),
            "tools/call" => handle_tools_call(msg.get("params")),
            "resources/list" => handle_resources_list(),
            "resources/read" => handle_resources_read(msg.get("params")),
            _ => Err((-32601, format!("Method not found: {method}"))),
        };

        match result {
            Ok(result_val) => write_result(&stdout, &id, &result_val),
            Err((code, msg)) => write_error(&stdout, id, code, &msg),
        }
    }

    eprintln!("[rush-mcp] Server shutting down (EOF)");
}

// ── initialize ──────────────────────────────────────────────────────

fn handle_initialize() -> Result<JsonValue, (i32, String)> {
    Ok(json!({
        "protocolVersion": "2024-11-05",
        "capabilities": {
            "tools": {},
            "resources": {}
        },
        "serverInfo": {
            "name": "rush-local",
            "version": VERSION
        },
        "instructions": INSTRUCTIONS
    }))
}

// ── tools/list ──────────────────────────────────────────────────────

fn handle_tools_list() -> Result<JsonValue, (i32, String)> {
    Ok(json!({
        "tools": [
            {
                "name": "rush_execute",
                "description": "Execute a command in the persistent Rush shell session. Supports Rush syntax, Unix shell commands, and pipeline operators. Variables, cwd, and environment persist across calls.",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "command": {
                            "type": "string",
                            "description": "The command to execute (Rush syntax or shell command)"
                        }
                    },
                    "required": ["command"]
                }
            },
            {
                "name": "rush_read_file",
                "description": "Read a file and return its content. Text files return UTF-8 content; binary files return base64. Includes MIME type and size metadata.",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "path": {
                            "type": "string",
                            "description": "Path to the file to read (absolute or relative to cwd)"
                        }
                    },
                    "required": ["path"]
                }
            },
            {
                "name": "rush_context",
                "description": "Get current shell context: hostname, cwd, git branch/dirty status, last exit code.",
                "inputSchema": {
                    "type": "object",
                    "properties": {}
                }
            }
        ]
    }))
}

// ── tools/call ──────────────────────────────────────────────────────

fn handle_tools_call(params: Option<&JsonValue>) -> Result<JsonValue, (i32, String)> {
    let params = params.ok_or((-32602, "Missing params".to_string()))?;
    let tool_name = params
        .get("name")
        .and_then(|n| n.as_str())
        .ok_or((-32602, "Missing tool name".to_string()))?;
    let arguments = params.get("arguments");

    let (result_json, is_error) = match tool_name {
        "rush_execute" => {
            let command = arguments
                .and_then(|a| a.get("command"))
                .and_then(|c| c.as_str())
                .ok_or((-32602, "Missing required parameter: command".to_string()))?;
            let result = llm::execute_one(command);
            let is_err = result.status != "success";
            (serde_json::to_value(&result).unwrap_or(json!(null)), is_err)
        }
        "rush_read_file" => {
            let path = arguments
                .and_then(|a| a.get("path"))
                .and_then(|p| p.as_str())
                .ok_or((-32602, "Missing required parameter: path".to_string()))?;
            let cwd = llm::get_cwd();
            let result = llm::lcat(path, &cwd);
            let is_err = result.status != "success";
            (serde_json::to_value(&result).unwrap_or(json!(null)), is_err)
        }
        "rush_context" => {
            let cwd = llm::get_cwd();
            let (branch, dirty) = llm::get_git_info(&cwd);
            let ctx = llm::LlmContext {
                ready: true,
                host: llm::get_hostname(),
                user: llm::get_username(),
                cwd,
                git_branch: branch,
                git_dirty: dirty,
                last_exit_code: 0,
                shell: "rush".into(),
                version: VERSION.into(),
            };
            (serde_json::to_value(&ctx).unwrap_or(json!(null)), false)
        }
        _ => return Err((-32602, format!("Unknown tool: {tool_name}"))),
    };

    // MCP wraps tool output in content array
    Ok(json!({
        "content": [{
            "type": "text",
            "text": result_json.to_string()
        }],
        "isError": is_error
    }))
}

// ── resources/list ──────────────────────────────────────────────────

fn handle_resources_list() -> Result<JsonValue, (i32, String)> {
    Ok(json!({
        "resources": [{
            "uri": "rush://lang-spec",
            "name": "Rush Language Specification",
            "mimeType": "text/yaml"
        }]
    }))
}

// ── resources/read ──────────────────────────────────────────────────

fn handle_resources_read(params: Option<&JsonValue>) -> Result<JsonValue, (i32, String)> {
    let uri = params
        .and_then(|p| p.get("uri"))
        .and_then(|u| u.as_str())
        .ok_or((-32602, "Missing uri parameter".to_string()))?;

    if uri != "rush://lang-spec" {
        return Err((-32602, format!("Unknown resource: {uri}")));
    }

    Ok(json!({
        "contents": [{
            "uri": "rush://lang-spec",
            "mimeType": "text/yaml",
            "text": LANG_SPEC
        }]
    }))
}

// ── JSON-RPC helpers ────────────────────────────────────────────────

fn write_result(stdout: &std::io::Stdout, id: &JsonValue, result: &JsonValue) {
    let response = json!({
        "jsonrpc": "2.0",
        "id": id,
        "result": result
    });
    let mut out = stdout.lock();
    serde_json::to_writer(&mut out, &response).ok();
    out.write_all(b"\n").ok();
    out.flush().ok();
}

fn write_error(stdout: &std::io::Stdout, id: JsonValue, code: i32, message: &str) {
    let response = json!({
        "jsonrpc": "2.0",
        "id": id,
        "error": {
            "code": code,
            "message": message
        }
    });
    let mut out = stdout.lock();
    serde_json::to_writer(&mut out, &response).ok();
    out.write_all(b"\n").ok();
    out.flush().ok();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn initialize_response() {
        let result = handle_initialize().unwrap();
        assert_eq!(result["protocolVersion"], "2024-11-05");
        assert_eq!(result["serverInfo"]["name"], "rush-local");
        assert!(result["instructions"].as_str().unwrap().contains("Rush"));
    }

    #[test]
    fn tools_list_has_three_tools() {
        let result = handle_tools_list().unwrap();
        let tools = result["tools"].as_array().unwrap();
        assert_eq!(tools.len(), 3);
        let names: Vec<&str> = tools.iter().map(|t| t["name"].as_str().unwrap()).collect();
        assert!(names.contains(&"rush_execute"));
        assert!(names.contains(&"rush_read_file"));
        assert!(names.contains(&"rush_context"));
    }

    #[test]
    fn tools_call_execute() {
        let params = json!({"name": "rush_execute", "arguments": {"command": "echo hello"}});
        let result = handle_tools_call(Some(&params)).unwrap();
        let content = result["content"][0]["text"].as_str().unwrap();
        let parsed: serde_json::Value = serde_json::from_str(content).unwrap();
        assert_eq!(parsed["status"], "success");
        assert_eq!(parsed["stdout"], "hello");
    }

    #[test]
    fn tools_call_rush_expr() {
        let params = json!({"name": "rush_execute", "arguments": {"command": "puts 1 + 2"}});
        let result = handle_tools_call(Some(&params)).unwrap();
        let content = result["content"][0]["text"].as_str().unwrap();
        let parsed: serde_json::Value = serde_json::from_str(content).unwrap();
        assert_eq!(parsed["status"], "success");
        assert_eq!(parsed["stdout"], "3");
    }

    #[test]
    fn tools_call_read_file() {
        let tmp = std::env::temp_dir().join("rush_mcp_test.txt");
        std::fs::write(&tmp, "mcp test content").unwrap();
        let params = json!({"name": "rush_read_file", "arguments": {"path": tmp.to_string_lossy()}});
        let result = handle_tools_call(Some(&params)).unwrap();
        let content = result["content"][0]["text"].as_str().unwrap();
        let parsed: serde_json::Value = serde_json::from_str(content).unwrap();
        assert_eq!(parsed["status"], "success");
        assert_eq!(parsed["content"], "mcp test content");
        assert_eq!(parsed["mime"], "text/plain");
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn tools_call_context() {
        let params = json!({"name": "rush_context", "arguments": {}});
        let result = handle_tools_call(Some(&params)).unwrap();
        let content = result["content"][0]["text"].as_str().unwrap();
        let parsed: serde_json::Value = serde_json::from_str(content).unwrap();
        assert_eq!(parsed["shell"], "rush");
        assert!(parsed["cwd"].as_str().unwrap().len() > 0);
    }

    #[test]
    fn tools_call_unknown_tool() {
        let params = json!({"name": "nonexistent", "arguments": {}});
        let result = handle_tools_call(Some(&params));
        assert!(result.is_err());
    }

    #[test]
    fn resources_list() {
        let result = handle_resources_list().unwrap();
        let resources = result["resources"].as_array().unwrap();
        assert_eq!(resources.len(), 1);
        assert_eq!(resources[0]["uri"], "rush://lang-spec");
    }

    #[test]
    fn resources_read_lang_spec() {
        let params = json!({"uri": "rush://lang-spec"});
        let result = handle_resources_read(Some(&params)).unwrap();
        let text = result["contents"][0]["text"].as_str().unwrap();
        assert!(text.contains("Rush Language Spec"));
        assert!(text.len() > 100);
    }

    #[test]
    fn resources_read_unknown() {
        let params = json!({"uri": "rush://unknown"});
        let result = handle_resources_read(Some(&params));
        assert!(result.is_err());
    }
}
