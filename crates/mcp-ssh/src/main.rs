//! `mcp-ssh` — MCP gateway exposing remote shells to AI agents.
//!
//! Launched by an agent harness (e.g. Claude Code) via stdio:
//!
//!     claude mcp add ssh -- mcp-ssh
//!
//! Each tool call carries a `host` parameter; the gateway opens (and
//! reuses) a per-host SSH connection and runs the requested command
//! there. Pairs with `mcp-local` (same wire protocol, local target).
//!
//! All routing + connection-pool logic lives in `rush_core::mcp_ssh`;
//! this main is just the binary entry point so any agent can launch
//! the gateway without going through `rush --mcp-ssh`.

fn main() {
    rush_core::mcp_ssh::run();
}
