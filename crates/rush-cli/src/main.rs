use rush_core::lexer::Lexer;
use rush_core::parser;
use std::io::{self, BufRead, Write};

fn main() {
    let args: Vec<String> = std::env::args().collect();

    // rush --lex: dump tokens
    if args.get(1).is_some_and(|a| a == "--lex") {
        let input = read_input(args.get(2));
        let tokens = Lexer::new(&input).tokenize();
        for token in &tokens {
            println!("{token}");
        }
        return;
    }

    // rush --parse: dump AST
    if args.get(1).is_some_and(|a| a == "--parse") {
        let input = read_input(args.get(2));
        match parser::parse(&input) {
            Ok(nodes) => {
                for node in &nodes {
                    println!("{node:#?}");
                }
            }
            Err(e) => {
                eprintln!("Parse error: {e}");
                std::process::exit(1);
            }
        }
        return;
    }

    // Default: interactive token/parse REPL
    println!("rush-rust 0.1.0 (lexer + parser)");
    print!("» ");
    io::stdout().flush().ok();

    for line in io::stdin().lock().lines() {
        let line = line.unwrap_or_default();
        if line == "exit" || line == "quit" {
            break;
        }
        match parser::parse(&line) {
            Ok(nodes) => {
                for node in &nodes {
                    println!("  {node:?}");
                }
            }
            Err(e) => eprintln!("  error: {e}"),
        }
        print!("» ");
        io::stdout().flush().ok();
    }
}

fn read_input(file: Option<&String>) -> String {
    if let Some(file) = file {
        std::fs::read_to_string(file).unwrap_or_else(|e| {
            eprintln!("rush: cannot read {file}: {e}");
            std::process::exit(1);
        })
    } else {
        let mut buf = String::new();
        io::Read::read_to_string(&mut io::stdin().lock(), &mut buf).ok();
        buf
    }
}
