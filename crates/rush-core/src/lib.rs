pub mod token;
pub mod lexer;
pub mod ast;
pub mod parser;
pub mod value;
pub mod env;
pub mod eval;
pub mod process;
pub mod stdlib;
pub mod llm;
pub mod pipeline;
pub mod ai;
pub mod mcp;
pub mod config;
pub mod triage;
pub mod dispatch;
pub mod theme;
pub mod trap;
pub mod jobs;
pub mod platform;
pub mod flags;
pub mod hints;
pub mod sync;
pub mod mcp_ssh;
pub mod mcp_install;
#[cfg(test)]
mod posix_tests;
#[cfg(test)]
mod integration_tests;
