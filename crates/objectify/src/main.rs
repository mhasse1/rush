//! `objectify` — turn columnar shell output into a JSON array of records.

use std::io::{self, Read};

const HELP: &str = "\
objectify — turn columnar shell output into a JSON array of records

USAGE:
    <columnar text> | objectify [OPTIONS]

OPTIONS:
    -h, --help     show this help

INPUT FORMAT:
    First non-empty line is the header (whitespace-split column names).
    Subsequent non-empty lines are records. Each record splits into N
    whitespace-separated tokens (N = column count); the LAST column
    captures all remaining text, so commands like `ps aux` whose
    COMMAND column has internal spaces round-trip correctly.

    Values that parse as integer or float become JSON numbers;
    everything else is a string.

OUTPUT:
    A single JSON array of objects on stdout. Stream into `jq` to
    filter / select / sort / aggregate.

EXAMPLES:
    ps aux | objectify | jq '.[] | select(.\"%CPU\" > 5)'
    df -h | objectify | jq 'sort_by(.Use)'
    docker ps | objectify | jq '.[] | .NAMES'
    ls -la | tail -n +2 | objectify    # skip the `total NNN` line

NOTES:
    Reads stdin only. No flags beyond --help today. Reasonable for any
    column-aligned table; not a CSV/TSV parser — use `jq -R` if your
    input already has structured separators.
";

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|a| a == "-h" || a == "--help") {
        println!("{HELP}");
        return;
    }
    let mut input = String::new();
    if let Err(e) = io::stdin().read_to_string(&mut input) {
        eprintln!("objectify: read stdin: {e}");
        std::process::exit(1);
    }
    println!("{}", rush_core::pipeline::objectify_text_to_json(&input));
}
