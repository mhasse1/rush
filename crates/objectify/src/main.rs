//! `objectify` — turn columnar shell output into a JSON array of records.
//!
//! Reads text from stdin (first line = headers, subsequent lines =
//! whitespace-separated records, last column captures remaining text),
//! emits a JSON array of objects on stdout. Pairs with `jq` for the
//! structured-data middle of any pipeline:
//!
//!     ps aux | objectify | jq '.[] | select(.CPU > 5)'
//!     df -h | objectify | jq 'sort_by(.Use)'
//!     git log --oneline | objectify
//!
//! Mirrors the rush built-in `| objectify` pipeline op so the same
//! transform is available from any shell.

use std::io::{self, Read};

fn main() {
    let mut input = String::new();
    if let Err(e) = io::stdin().read_to_string(&mut input) {
        eprintln!("objectify: read stdin: {e}");
        std::process::exit(1);
    }
    println!("{}", rush_core::pipeline::objectify_text_to_json(&input));
}
