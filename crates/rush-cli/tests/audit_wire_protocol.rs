//! Subprocess-level audit tests for `rush --llm` and `rush --mcp`.
//!
//! Each test here corresponds to an invariant in internal/llm-mcp-audit.md
//! and guards against regression of an audit finding. Tests spawn the real
//! rush-cli binary and exercise the JSON wire protocol end-to-end — they
//! are slower than unit tests but they are the only way to test the actual
//! contract an agent harness sees.
//!
//! Naming: `mode__invariant_being_checked`.
//!   mode = llm or mcp (which wire protocol)
//!   invariant = the audit claim being verified
//!
//! Corresponding audit findings noted in doc comments on each test.

// Intentional double-underscore separator: mode__invariant.
#![allow(non_snake_case)]

use serde_json::{json, Value};
use std::io::Write;
use std::process::{Command, Stdio};
use std::time::Duration;

const RUSH: &str = env!("CARGO_BIN_EXE_rush-cli");

// ── helpers ─────────────────────────────────────────────────────────

/// Send a batch of lines to a rush subprocess over stdin, collect
/// stdout/stderr, and return parsed JSON objects from stdout
/// (one per non-empty line).
fn drive(args: &[&str], stdin: &str) -> (Vec<Value>, String, i32) {
    let mut child = Command::new(RUSH)
        .args(args)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn rush-cli");

    {
        let sin = child.stdin.as_mut().expect("stdin");
        sin.write_all(stdin.as_bytes()).expect("write stdin");
    }

    // Close stdin so rush sees EOF.
    drop(child.stdin.take());

    // Bounded wait — any audit test that hangs >10s is a bug in the test
    // (or a regression in rush that deserves attention, not a hang).
    let start = std::time::Instant::now();
    loop {
        if let Some(status) = child.try_wait().expect("try_wait") {
            let out = child.wait_with_output().expect("wait_with_output");
            let stdout = String::from_utf8_lossy(&out.stdout).to_string();
            let stderr = String::from_utf8_lossy(&out.stderr).to_string();
            let objs: Vec<Value> = stdout
                .lines()
                .filter(|l| !l.trim().is_empty())
                .filter_map(|l| serde_json::from_str(l).ok())
                .collect();
            return (objs, stderr, status.code().unwrap_or(-1));
        }
        if start.elapsed() > Duration::from_secs(10) {
            let _ = child.kill();
            panic!("rush subprocess did not terminate within 10s");
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}

/// Build a JSON-RPC 2.0 request line.
fn rpc(id: u64, method: &str, params: Value) -> String {
    let msg = json!({"jsonrpc": "2.0", "id": id, "method": method, "params": params});
    format!("{msg}\n")
}

/// Extract the LlmResult from an MCP tools/call response.
/// MCP wraps tool output in content[0].text as a JSON string.
fn unwrap_mcp_tool_result(response: &Value) -> Value {
    let text = response["result"]["content"][0]["text"]
        .as_str()
        .expect("content[0].text present");
    serde_json::from_str(text).expect("text is valid JSON")
}

// ── F1: session state persists across calls ─────────────────────────

/// Audit: F1 — variables set in one MCP tools/call survive to the next.
#[test]
fn mcp__variable_persists_across_tools_calls() {
    let stdin = format!(
        "{}{}",
        rpc(1, "tools/call", json!({"name":"rush_execute","arguments":{"command":"x = 42"}})),
        rpc(2, "tools/call", json!({"name":"rush_execute","arguments":{"command":"puts x"}})),
    );
    let (objs, _stderr, _code) = drive(&["--mcp"], &stdin);
    assert_eq!(objs.len(), 2, "expected 2 responses, got {objs:?}");
    let r2 = unwrap_mcp_tool_result(&objs[1]);
    assert_eq!(r2["status"], "success");
    assert_eq!(r2["stdout"], "42", "x must have persisted from call 1");
}

/// Audit: F1 — function definitions survive across MCP tools/call.
#[test]
fn mcp__function_def_persists_across_tools_calls() {
    let stdin = format!(
        "{}{}",
        rpc(1, "tools/call", json!({"name":"rush_execute","arguments":{"command":"def dbl(n); return n * 2; end"}})),
        rpc(2, "tools/call", json!({"name":"rush_execute","arguments":{"command":"puts dbl(21)"}})),
    );
    let (objs, _stderr, _code) = drive(&["--mcp"], &stdin);
    assert_eq!(objs.len(), 2);
    let r2 = unwrap_mcp_tool_result(&objs[1]);
    assert_eq!(r2["status"], "success");
    assert_eq!(r2["stdout"], "42");
}

/// Audit: F1 — `rush --llm` subprocess preserves variables across stdin lines.
#[test]
fn llm__variable_persists_across_lines() {
    let stdin = "x = 7\nputs x * x\n";
    let (objs, _stderr, _code) = drive(&["--llm"], stdin);
    // objs[0] is the opening context; results follow.
    let puts_result = objs
        .iter()
        .find(|v| v["stdout"] == "49")
        .expect("expected a result with stdout = 49");
    assert_eq!(puts_result["status"], "success");
}

// ── F2: wire-protocol shape ─────────────────────────────────────────

/// Audit: F2 — `rush --llm` emits LlmContext exactly once at startup,
/// not before every command.
#[test]
fn llm__context_emitted_only_at_startup() {
    let stdin = "puts 1\nputs 2\nputs 3\n";
    let (objs, _stderr, _code) = drive(&["--llm"], stdin);
    let context_count = objs.iter().filter(|v| v.get("ready").is_some()).count();
    assert_eq!(
        context_count, 1,
        "expected exactly one LlmContext at startup, got {context_count}; objs={objs:?}"
    );
}

/// Audit: F2 — every LlmResult carries host and session_id.
#[test]
fn llm__result_carries_identity_fields() {
    let stdin = "puts 1\nputs 2\n";
    let (objs, _stderr, _code) = drive(&["--llm"], stdin);
    for obj in objs.iter().skip(1) {
        // skip the opening LlmContext
        assert!(
            obj["host"].is_string() && !obj["host"].as_str().unwrap().is_empty(),
            "result missing host: {obj}"
        );
        assert!(
            obj["session_id"].is_string() && obj["session_id"].as_str().unwrap().len() == 16,
            "result missing or malformed session_id: {obj}"
        );
    }
}

/// Audit: F2 — session_id is stable across all turns of one session.
#[test]
fn llm__session_id_stable_within_session() {
    let stdin = "puts 1\nputs 2\nputs 3\n";
    let (objs, _stderr, _code) = drive(&["--llm"], stdin);
    let ids: Vec<&str> = objs
        .iter()
        .filter_map(|v| v["session_id"].as_str())
        .collect();
    assert!(!ids.is_empty(), "no session_id found in any response");
    let first = ids[0];
    for id in &ids {
        assert_eq!(*id, first, "session_id must be stable; got {ids:?}");
    }
}

/// Audit: F2 — distinct processes produce distinct session_ids.
#[test]
fn llm__session_id_unique_across_processes() {
    let (objs_a, _, _) = drive(&["--llm"], "puts 1\n");
    let (objs_b, _, _) = drive(&["--llm"], "puts 1\n");
    let id_a = objs_a[0]["session_id"].as_str().expect("session_id a");
    let id_b = objs_b[0]["session_id"].as_str().expect("session_id b");
    assert_ne!(id_a, id_b, "two distinct processes produced the same session_id");
    assert_eq!(id_a.len(), 16);
    assert_eq!(id_b.len(), 16);
}

/// Audit: F2 — MCP tool result carries session_id so the harness can
/// verify which session replied.
#[test]
fn mcp__tool_result_carries_session_id() {
    let stdin = rpc(1, "tools/call", json!({"name":"rush_execute","arguments":{"command":"puts 1"}}));
    let (objs, _stderr, _code) = drive(&["--mcp"], &stdin);
    let r = unwrap_mcp_tool_result(&objs[0]);
    let sid = r["session_id"].as_str().expect("session_id field");
    assert_eq!(sid.len(), 16, "session_id should be 16 hex chars");
    assert!(sid.chars().all(|c| c.is_ascii_hexdigit()), "session_id hex only");
}

// ── F3: exit/quit behavior ──────────────────────────────────────────

/// Audit: F3 — `exit` in --llm terminates the session cleanly.
#[test]
fn llm__exit_terminates_session() {
    let stdin = "puts \"before\"\nexit\nputs \"should-not-run\"\n";
    let (objs, _stderr, _code) = drive(&["--llm"], stdin);
    // There should be no result containing "should-not-run".
    let leaked = objs
        .iter()
        .any(|v| v["stdout"].as_str() == Some("should-not-run"));
    assert!(!leaked, "commands after exit should not execute; objs={objs:?}");
}

/// Audit: F3 — `exit N` in --llm returns the requested code.
#[test]
fn llm__exit_with_code_preserves_code() {
    let stdin = "exit 7\n";
    let (objs, _stderr, _code) = drive(&["--llm"], stdin);
    let exit_result = objs
        .iter()
        .find(|v| v.get("exit_code").and_then(|c| c.as_i64()) == Some(7))
        .expect("expected a result with exit_code = 7");
    assert_eq!(exit_result["status"], "error");
}

/// Audit: F3 — MCP server does NOT terminate when a tools/call runs `exit`.
#[test]
fn mcp__exit_in_tool_does_not_kill_server() {
    let stdin = format!(
        "{}{}",
        rpc(1, "tools/call", json!({"name":"rush_execute","arguments":{"command":"exit"}})),
        rpc(2, "tools/call", json!({"name":"rush_execute","arguments":{"command":"echo still-alive"}})),
    );
    let (objs, _stderr, _code) = drive(&["--mcp"], &stdin);
    assert_eq!(objs.len(), 2, "server should respond to both calls");
    let r2 = unwrap_mcp_tool_result(&objs[1]);
    assert_eq!(r2["stdout"], "still-alive", "server must survive the exit call");
}
