//! `ai` — query an LLM from any shell.

use std::io::{self, IsTerminal, Read};

const HELP: &str = "\
ai — query an LLM from the command line

USAGE:
    ai [OPTIONS] \"prompt\"
    ai --agent \"task description\" [OPTIONS]
    <stdin> | ai [OPTIONS] \"prompt\"

OPTIONS:
    -p, --provider <NAME>   anthropic (default) | openai | gemini | ollama
    -m, --model <ID>        override provider's default model (e.g. claude-sonnet-4-5)
    --agent                 run as an agentic loop instead of single-shot Q&A
    -h, --help              show this help

EXAMPLES:
    ai \"summarize the rust ownership model\"
    ai -p openai -m gpt-4o \"...\"
    cat README.md | ai \"what does this do?\"
    ps aux | objectify | jq '.[] | select(.\"%CPU\" > 5)' | ai \"explain these processes\"

ENVIRONMENT:
    ANTHROPIC_API_KEY       required when --provider anthropic (default)
    OPENAI_API_KEY          required when --provider openai
    GEMINI_API_KEY          required when --provider gemini
    (ollama runs locally and needs no key)

NOTES:
    Streams to stdout as tokens arrive. Exits non-zero on provider error.
    Reads stdin only when piped — interactive `ai \"hi\"` does not block.
";

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() || args.iter().any(|a| a == "-h" || a == "--help") {
        println!("{HELP}");
        return;
    }

    let joined = args.join(" ");

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
            eprintln!("usage: ai --agent \"task\" [-p provider] [-m model] (try --help)");
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
        eprintln!("usage: ai \"prompt\" [-p provider] [-m model] (try --help)");
        std::process::exit(2);
    }

    if let Err(e) = rush_core::ai::execute(
        provider.as_deref(),
        model.as_deref(),
        &prompt,
        piped.as_deref(),
        &rush_core::ai::build_generic_system_prompt(),
    ) {
        eprintln!("ai: {e}");
        std::process::exit(1);
    }
}
