//! Demo / smoke-test binary for the rush-line painter.
//!
//! Runs an interactive prompt loop using rush-line's `LineEditor`.
//! Each iteration: render a multi-line prompt (mimicking rush's), read
//! a line, echo it back, repeat. Type `exit` (or Ctrl-D on an empty
//! buffer) to quit.
//!
//! Run with:
//!     cargo run --example demo -p rush-line
//!
//! Stress patterns to verify against #270:
//!
//! - Type many characters in a row. Each keystroke should NOT eat a
//!   line of scrollback above the prompt.
//! - Press a long line that wraps. Backspace through it. Re-type.
//!   Scrollback above should remain intact.
//! - Run several iterations, each printing a few lines of fake "command
//!   output" via the echo. Compare scrollback to bash with a
//!   multi-line PS1 doing the same thing.
//! - Resize the terminal narrower / wider mid-edit. The current input
//!   should re-flow without trashing scrollback above the prompt.

use std::io::Write;

use rush_line::{LineEditor, Prompt, Signal};

struct DemoPrompt {
    iteration: usize,
}

impl Prompt for DemoPrompt {
    fn render(&self) -> String {
        // Mimic rush's prompt shape: leading blank line for breathing
        // space, status line with iteration counter (stand-in for the
        // ✓/exit-code, time, host, cwd, git block), trailing newline,
        // then a 2-char indicator on the next row. This is the exact
        // structure that exercised the line-eating bug in rush#270.
        format!(
            "\n\x1b[32m✓\x1b[0m \
             \x1b[36m{:02}:{:02}\x1b[0m \
             \x1b[2m#{}\x1b[0m\n\
             \x1b[2m»\x1b[0m ",
            (self.iteration / 60) % 24,
            self.iteration % 60,
            self.iteration,
        )
    }
}

fn main() -> std::io::Result<()> {
    let mut editor = LineEditor::new();
    let mut iteration = 0usize;

    println!("rush-line demo. Type `exit` or hit Ctrl-D on an empty line to quit.");
    println!("Stress test: rapid typing, long lines, backspace, terminal resize.");

    loop {
        iteration += 1;
        let prompt = DemoPrompt { iteration };
        match editor.read_line(&prompt)? {
            Signal::Success(line) => {
                let trimmed = line.trim();
                if trimmed == "exit" || trimmed == "quit" {
                    break;
                }
                if trimmed.is_empty() {
                    continue;
                }
                // Print a few rows of "fake command output" so we can
                // see scrollback behavior across iterations.
                println!("you typed: {trimmed}");
                println!("(length: {} bytes)", line.len());
                std::io::stdout().flush().ok();
            }
            Signal::CtrlC => {
                // Common shell convention: Ctrl-C clears the line and
                // returns a fresh prompt.
                println!("^C");
            }
            Signal::CtrlD => {
                println!();
                break;
            }
        }
    }

    println!("bye.");
    Ok(())
}
