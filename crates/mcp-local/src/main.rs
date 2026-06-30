//! `mcp-local` — MCP server exposing the local shell to AI agents.
//!
//! Launched by an agent harness (e.g. Claude Code) via stdio:
//!
//!     claude mcp add local -- mcp-local
//!
//! Speaks JSON-RPC 2.0 over stdin/stdout. All protocol + dispatch
//! logic lives in `rush_core::mcp`; this main is just the entry point.
//!
//! Uses toolkit-flavored handshake instructions so connecting LLMs are
//! taught about the toolkit family (`objectify`, `ai`, `jq`) instead
//! of being told they're in a rush shell session.

fn main() {
    rush_core::mcp::run_with_instructions(rush_core::mcp::TOOLKIT_INSTRUCTIONS);
}
