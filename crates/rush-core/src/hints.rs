//! Training hints — contextual suggestions after failed commands.
//! Helps users learn Rush syntax and discover builtins.

/// Check if we should show a hint for a failed command.
/// Returns a suggestion string if applicable.
pub fn hint_for_command(line: &str, exit_code: i32) -> Option<String> {
    let first_word = line.split_whitespace().next()?;

    // Command not found (127)
    if exit_code == 127 {
        return hint_not_found(first_word);
    }

    // Permission denied (126)
    if exit_code == 126 {
        return Some(format!("hint: try 'chmod +x {first_word}' or 'sudo {line}'"));
    }

    // Bash-isms → Rush equivalents
    hint_bashism(line)
}

fn hint_not_found(cmd: &str) -> Option<String> {
    match cmd {
        // Common typos
        "claer" | "cler" => Some("hint: did you mean 'clear'?".into()),
        "gti" | "got" => Some("hint: did you mean 'git'?".into()),
        "sl" => Some("hint: did you mean 'ls'?".into()),
        "suod" | "sduo" => Some("hint: did you mean 'sudo'?".into()),

        // Package managers
        "apt" | "apt-get" if cfg!(target_os = "macos") => {
            Some("hint: macOS uses 'brew install' instead of apt".into())
        }
        "yum" | "dnf" if cfg!(target_os = "macos") => {
            Some("hint: macOS uses 'brew install' instead of yum/dnf".into())
        }
        "brew" if cfg!(target_os = "linux") => {
            Some("hint: try 'apt install' or 'dnf install' on Linux".into())
        }

        // Common missing tools
        "python" => Some("hint: try 'python3' — Python 2 is often not installed".into()),
        "pip" => Some("hint: try 'pip3' or 'python3 -m pip'".into()),
        "node" => Some("hint: install Node.js with 'brew install node' or 'nvm install --lts'".into()),

        // Rush-specific
        _ => {
            // Check if it looks like they tried a Rush method on a variable
            if cmd.contains('.') {
                Some(format!("hint: '{cmd}' — try assigning to a variable first: x = ...; x.method()"))
            } else {
                Some(format!("hint: '{cmd}' not found. Check 'path check' or 'which {cmd}'"))
            }
        }
    }
}

fn hint_bashism(line: &str) -> Option<String> {
    // $variable → variable (no $ in Rush)
    if line.contains("$") && !line.contains("$(") && !line.contains("$((") {
        let trimmed = line.trim();
        if trimmed.starts_with("echo $") || trimmed.starts_with("if [ $") {
            return Some("hint: in Rush, variables don't need $: use 'puts name' not 'echo $name'".into());
        }
    }

    // [ -f file ] → File.exist?
    if line.contains("[ -f ") || line.contains("test -f ") {
        return Some("hint: in Rush, use File.exist?(\"path\") instead of [ -f path ]".into());
    }

    // [ -d dir ] → Dir.exist?
    if line.contains("[ -d ") || line.contains("test -d ") {
        return Some("hint: in Rush, use Dir.exist?(\"path\") instead of [ -d path ]".into());
    }

    // fi → end
    if line.trim() == "fi" || line.trim() == "done" || line.trim() == "esac" {
        return Some("hint: Rush uses 'end' instead of 'fi', 'done', or 'esac'".into());
    }

    // then → (not needed)
    if line.trim() == "then" {
        return Some("hint: Rush doesn't use 'then' — just put the body after the condition".into());
    }

    None
}
