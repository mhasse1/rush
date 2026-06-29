//! `mcp-local` — MCP server exposing the local shell to AI agents.
//!
//! Launched by an agent harness (e.g. Claude Code) via stdio:
//!
//!     claude mcp add local -- mcp-local
//!
//! Speaks JSON-RPC 2.0 over stdin/stdout. All protocol + dispatch
//! logic lives in `rush_core::mcp`; this main is just the binary
//! entry point so any agent can launch the server without going
//! through `rush --mcp`.
//!
//! Replaces `rush --mcp` in the toolkit pivot — same wire protocol,
//! same handlers, distributable independent of the rush shell.

fn main() {
    rush_core::mcp::run();
}
