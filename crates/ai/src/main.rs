//! `ai` — query an LLM from any shell.
//!
//! Usage:
//!     ai "summarize the rust ownership model"
//!     ai --provider openai "..."
//!     ai --model gpt-4o "..."
//!     ai --agent "investigate the failing CI run on this branch"
//!     cat README.md | ai "what does this do?"
//!
//! Mirrors rush's built-in `ai` command but ships as a standalone
//! binary so any shell (zsh, bash, fish, PowerShell) can drive it.
//! The actual provider plumbing, streaming, and agent loop all live
//! in `rush_core::ai`; this main is just the CLI surface.

use std::io::{self, IsTerminal, Read};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() {
        eprintln!("usage: ai [--agent] [-p provider] [-m model] \"prompt\"");
        std::process::exit(2);
    }

    // Quoted prompts arrive as single argv entries; joining with a
    // space and re-parsing keeps parse_ai_args in charge of the
    // [-p|-m] handling without us duplicating it here.
    let joined = args.join(" ");

    // Read stdin only if it's piped — `ai "hi"` on a tty shouldn't
    // block waiting for nonexistent input.
    let piped: Option<String> = if io::stdin().is_terminal() {
        None
    } else {
        let mut buf = String::new();
        io::stdin().read_to_string(&mut buf).ok().and_then(|_| {
            if buf.is_empty() { None } else { Some(buf) }
        })
    };

    if let Some(rest) = joined.strip_prefix("--agent") {
        let (prompt, provider, model) = rush_core::ai::parse_ai_args(rest.trim());
        if prompt.is_empty() {
            eprintln!("usage: ai --agent \"task\" [-p provider] [-m model]");
            std::process::exit(2);
        }
        if let Err(e) = rush_core::ai::execute_agent(
            provider.as_deref(),
            model.as_deref(),
            &prompt,
        ) {
            eprintln!("ai --agent: {e}");
            std::process::exit(1);
        }
        return;
    }

    let (prompt, provider, model) = rush_core::ai::parse_ai_args(&joined);
    if prompt.is_empty() {
        eprintln!("usage: ai \"prompt\" [-p provider] [-m model]");
        std::process::exit(2);
    }

    if let Err(e) = rush_core::ai::execute(
        provider.as_deref(),
        model.as_deref(),
        &prompt,
        piped.as_deref(),
    ) {
        eprintln!("ai: {e}");
        std::process::exit(1);
    }
}
