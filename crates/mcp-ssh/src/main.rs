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
//! Uses toolkit-flavored handshake instructions so connecting LLMs
//! are taught that the remote shell is whatever the user configured
//! (zsh/bash/etc.) and which toolkit helpers may be available there.

fn main() {
    rush_core::mcp_ssh::run_with_instructions(rush_core::mcp_ssh::TOOLKIT_INSTRUCTIONS);
}
