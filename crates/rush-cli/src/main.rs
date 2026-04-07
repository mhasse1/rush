use rush_core::lexer::Lexer;
use std::io::{self, BufRead, Write};

fn main() {
    let args: Vec<String> = std::env::args().collect();

    // rush --parse: dump tokens as JSON (useful for tooling)
    if args.get(1).is_some_and(|a| a == "--parse") {
        let input = if let Some(file) = args.get(2) {
            std::fs::read_to_string(file).unwrap_or_else(|e| {
                eprintln!("rush: cannot read {file}: {e}");
                std::process::exit(1);
            })
        } else {
            let mut buf = String::new();
            io::stdin().lock().read_line(&mut buf).ok();
            buf
        };

        let tokens = Lexer::new(&input).tokenize();
        for token in &tokens {
            println!("{token}");
        }
        return;
    }

    // Default: print version
    println!("rush-rust 0.1.0 (lexer only)");
    print!("» ");
    io::stdout().flush().ok();

    for line in io::stdin().lock().lines() {
        let line = line.unwrap_or_default();
        if line == "exit" || line == "quit" {
            break;
        }
        let tokens = Lexer::new(&line).tokenize();
        for token in &tokens {
            println!("  {token}");
        }
        print!("» ");
        io::stdout().flush().ok();
    }
}
