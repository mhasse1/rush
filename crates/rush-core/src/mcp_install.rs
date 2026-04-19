//! MCP Installer — registers rush MCP servers into Claude Code and Claude Desktop.
//!
//! Usage: rush install mcp --claude
//!
//! Updates:
//!   ~/.claude.json                                          — Claude Code MCP servers
//!   ~/.claude/mcp.json                                      — Claude Code (legacy)
//!   ~/Library/Application Support/Claude/claude_desktop_config.json — Claude Desktop
//!   ~/.claude/settings.json                                 — Claude Code permissions

use serde_json::{json, Value as JsonValue};
use std::path::{Path, PathBuf};

const LOCAL_SERVER: &str = "rush-local";
const SSH_SERVER: &str = "rush-ssh";

const ALLOWED_TOOLS: &[&str] = &[
    "mcp__rush-local__rush_execute",
    "mcp__rush-local__rush_read_file",
    "mcp__rush-local__rush_context",
    "mcp__rush-ssh__rush_execute",
    "mcp__rush-ssh__rush_read_file",
    "mcp__rush-ssh__rush_context",
];

const OLD_TOOLS: &[&str] = &[
    "mcp__rush__rush_execute",
    "mcp__rush__rush_read_file",
    "mcp__rush__rush_context",
];

pub fn install_claude() {
    let rush_path = get_rush_binary_path();
    eprintln!("Installing Rush MCP servers into Claude...");
    eprintln!("  Binary: {rush_path}");
    eprintln!();

    // 1. ~/.claude.json (where Claude Code reads user-level MCP servers)
    let claude_json = home_path(".claude.json");
    if claude_json.exists() {
        update_claude_json(&claude_json, &rush_path);
    } else {
        eprintln!("   - ~/.claude.json not found (run Claude Code first, then re-run this)");
    }

    // 2. ~/.claude/mcp.json (legacy location)
    let mcp_json = home_path(".claude/mcp.json");
    ensure_parent(&mcp_json);
    update_mcp_config(&mcp_json, &rush_path, "Claude Code (mcp.json)");

    // 3. Claude Desktop
    if let Some(desktop_path) = get_claude_desktop_path() {
        if desktop_path.exists() {
            update_mcp_config(&desktop_path, &rush_path, "Claude Desktop");
        } else {
            eprintln!("   - Claude Desktop config not found (skipped)");
        }
    }

    // 4. ~/.claude/settings.json (permissions)
    let settings_path = home_path(".claude/settings.json");
    ensure_parent(&settings_path);
    update_claude_settings(&settings_path);

    eprintln!();
    eprintln!("Done! Rush MCP servers installed.");
    eprintln!("  Servers: {LOCAL_SERVER} (persistent local), {SSH_SERVER} (SSH gateway)");
    eprintln!();
    eprintln!("Restart Claude Code / Claude Desktop to pick up the new servers.");
}

// ── Path helpers ───────────────────────────────────────────────────

fn home_dir() -> PathBuf {
    std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("/tmp"))
}

fn home_path(relative: &str) -> PathBuf {
    home_dir().join(relative)
}

fn ensure_parent(path: &Path) {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).ok();
    }
}

fn get_rush_binary_path() -> String {
    // Try to resolve the actual binary path
    if let Ok(exe) = std::env::current_exe() {
        // Follow symlinks
        if let Ok(resolved) = std::fs::canonicalize(&exe) {
            return resolved.to_string_lossy().to_string();
        }
        return exe.to_string_lossy().to_string();
    }
    // Fallback
    "/usr/local/bin/rush".to_string()
}

fn get_claude_desktop_path() -> Option<PathBuf> {
    let home = home_dir();
    #[cfg(target_os = "macos")]
    {
        Some(home.join("Library/Application Support/Claude/claude_desktop_config.json"))
    }
    #[cfg(target_os = "windows")]
    {
        std::env::var("APPDATA").ok()
            .map(|appdata| PathBuf::from(appdata).join("Claude/claude_desktop_config.json"))
    }
    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    {
        Some(home.join(".config/Claude/claude_desktop_config.json"))
    }
}

// ── JSON updaters ──────────────────────────────────────────────────

fn read_json(path: &PathBuf) -> JsonValue {
    std::fs::read_to_string(path)
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_else(|| json!({}))
}

fn write_json(path: &PathBuf, value: &JsonValue) -> bool {
    match serde_json::to_string_pretty(value) {
        Ok(s) => std::fs::write(path, s).is_ok(),
        Err(_) => false,
    }
}

/// Update ~/.claude.json — top-level mcpServers (where Claude Code reads them).
fn update_claude_json(path: &PathBuf, rush_path: &str) {
    let mut root = read_json(path);

    let obj = root.as_object_mut().unwrap();
    if !obj.contains_key("mcpServers") {
        obj.insert("mcpServers".into(), json!({}));
    }

    let servers = obj.get_mut("mcpServers").unwrap().as_object_mut().unwrap();

    // Clean up old entries
    servers.remove("rush");
    servers.remove(LOCAL_SERVER);
    servers.remove(SSH_SERVER);

    servers.insert(LOCAL_SERVER.into(), json!({
        "type": "stdio",
        "command": rush_path,
        "args": ["--mcp"],
        "env": {}
    }));

    servers.insert(SSH_SERVER.into(), json!({
        "type": "stdio",
        "command": rush_path,
        "args": ["--mcp-ssh"],
        "env": {}
    }));

    if write_json(path, &root) {
        eprintln!("   + Claude Code: {}", path.display());
    } else {
        eprintln!("   ! Failed to update {}", path.display());
    }
}

/// Register both servers in an MCP config file (mcp.json or claude_desktop_config.json).
fn update_mcp_config(path: &PathBuf, rush_path: &str, label: &str) {
    let mut root = read_json(path);

    let obj = root.as_object_mut().unwrap();
    if !obj.contains_key("mcpServers") {
        obj.insert("mcpServers".into(), json!({}));
    }

    let servers = obj.get_mut("mcpServers").unwrap().as_object_mut().unwrap();

    servers.remove("rush");
    servers.remove(LOCAL_SERVER);
    servers.remove(SSH_SERVER);

    servers.insert(LOCAL_SERVER.into(), json!({
        "command": rush_path,
        "args": ["--mcp"]
    }));

    servers.insert(SSH_SERVER.into(), json!({
        "command": rush_path,
        "args": ["--mcp-ssh"]
    }));

    if write_json(path, &root) {
        eprintln!("   + {label}: {}", path.display());
    } else {
        eprintln!("   ! Failed to update {label}: {}", path.display());
    }
}

/// Add rush MCP tools to Claude Code permissions allow list.
fn update_claude_settings(path: &PathBuf) {
    let mut root = read_json(path);

    let obj = root.as_object_mut().unwrap();
    if !obj.contains_key("permissions") {
        obj.insert("permissions".into(), json!({}));
    }
    let perms = obj.get_mut("permissions").unwrap().as_object_mut().unwrap();
    if !perms.contains_key("allow") {
        perms.insert("allow".into(), json!([]));
    }

    let allow = perms.get_mut("allow").unwrap().as_array_mut().unwrap();

    // Remove old "rush" permissions
    allow.retain(|v| {
        let s = v.as_str().unwrap_or("");
        !OLD_TOOLS.contains(&s)
    });

    // Collect existing for dedup
    let existing: Vec<String> = allow.iter()
        .filter_map(|v| v.as_str().map(|s| s.to_string()))
        .collect();

    let mut added = 0;
    for tool in ALLOWED_TOOLS {
        if !existing.contains(&tool.to_string()) {
            allow.push(json!(tool));
            added += 1;
        }
    }

    if write_json(path, &root) {
        if added > 0 {
            eprintln!("   + Added {added} rush tools to permissions: {}", path.display());
        } else {
            eprintln!("   + Rush tools already permitted: {}", path.display());
        }
    } else {
        eprintln!("   ! Failed to update {}", path.display());
    }
}
