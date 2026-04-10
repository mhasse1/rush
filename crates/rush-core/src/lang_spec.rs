//! Embedded Rush language specification — shared across AI-facing codepaths.

/// The Rush language specification, embedded at compile time from docs/rush-lang-spec.yaml.
/// Used by: MCP server (rush://lang-spec resource), ai builtin (system prompt),
/// and --llm mode (first connection context).
pub const LANG_SPEC: &str = include_str!("../../../docs/rush-lang-spec.yaml");
