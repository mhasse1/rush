//! Line triage: decide whether a line is Rush syntax or a shell command.
//! Mirrors the C# ScriptEngine.IsRushSyntax() logic.

/// Check if a line is Rush syntax (should be parsed) vs a shell command (should be executed via sh).
pub fn is_rush_syntax(input: &str) -> bool {
    let trimmed = input.trim();
    if trimmed.is_empty() {
        return false;
    }

    let first_word = trimmed
        .split(|c: char| c.is_whitespace() || c == '(')
        .next()
        .unwrap_or("");

    // Block-start keywords
    let block_keywords = [
        "if", "elsif", "else", "unless", "while", "until", "for", "loop",
        "def", "class", "enum", "case", "match", "try", "begin",
        "macos", "linux", "win64", "win32", "isssh",
        "do",
    ];
    // plugin.NAME is always Rush syntax
    if first_word.eq_ignore_ascii_case("plugin") {
        return true;
    }

    // ps/ps5 are block keywords only when bare (no args)
    if first_word.eq_ignore_ascii_case("ps") || first_word.eq_ignore_ascii_case("ps5") {
        if trimmed.len() == first_word.len() {
            return true; // bare "ps" or "ps5"
        }
        // "ps -ef", "ps aux" → shell command, fall through
    } else if block_keywords.iter().any(|k| k.eq_ignore_ascii_case(first_word)) {
        return true;
    }

    // Platform dot-notation: macos.version, linux.arch
    if let Some(dot) = first_word.find('.') {
        let base = &first_word[..dot];
        if block_keywords.iter().any(|k| k.eq_ignore_ascii_case(base)) {
            return true;
        }
    }

    // end, return, next, continue, break
    if ["end", "return", "next", "continue", "break"]
        .iter()
        .any(|k| k.eq_ignore_ascii_case(first_word))
    {
        return true;
    }

    // Built-in functions (can be called without parens)
    let builtins = ["puts", "print", "warn", "die", "ask", "sleep", "exit", "ai"];
    if builtins.iter().any(|b| b.eq_ignore_ascii_case(first_word)) {
        return true;
    }

    // Function call: word( — no shell command uses this syntax
    if trimmed.contains('(') {
        let paren_pos = trimmed.find('(').unwrap();
        let before = &trimmed[..paren_pos];
        if before.chars().all(|c| c.is_alphanumeric() || c == '_' || c == '.' || c == '?') && !before.is_empty() {
            return true;
        }
    }

    // Assignment: word = expr (but not ==, !=, >=, <=)
    // Exclude builtins that use = in their own syntax (export FOO=bar, alias k='v', etc.)
    let builtins_with_equals = [
        "export", "set", "alias", "cd", "unset", "wait", "printf", "read", "exec", "trap",
    ];
    let is_builtin_eq = builtins_with_equals.iter().any(|b| b.eq_ignore_ascii_case(first_word));

    if !is_builtin_eq {
        if let Some(eq_pos) = trimmed.find('=') {
            if eq_pos > 0 {
                let before_eq = trimmed[..eq_pos].trim();
                let after_eq = &trimmed[eq_pos..];
                // Not ==, !=, >=, <=
                if !after_eq.starts_with("==")
                    && !trimmed[..eq_pos].ends_with('!')
                    && !trimmed[..eq_pos].ends_with('>')
                    && !trimmed[..eq_pos].ends_with('<')
                {
                    // Check left side is identifier(s)
                    let is_ident = before_eq.split(',')
                        .all(|part| {
                            let p = part.trim();
                            !p.is_empty() && p.chars().all(|c| c.is_alphanumeric() || c == '_' || c == '.' || c == '$')
                        });
                    if is_ident {
                        return true;
                    }
                }
            }
        }
    }

    // Compound assignment: word += expr
    if trimmed.contains("+=") || trimmed.contains("-=") || trimmed.contains("*=") || trimmed.contains("/=") {
        return true;
    }

    // Stdlib receiver: File.xxx, Dir.xxx, Time.xxx, env.xxx
    let stdlib = ["file", "dir", "time", "env"];
    if stdlib.iter().any(|s| first_word.eq_ignore_ascii_case(s)) && first_word.contains('.') || trimmed.starts_with("File.") || trimmed.starts_with("Dir.") || trimmed.starts_with("Time.") || trimmed.starts_with("env.") {
        return true;
    }

    // Method call on first word: word.method( — only if first_word itself contains a dot
    // and the part after the dot looks like a method (alphabetic start)
    if first_word.contains('.') && !first_word.starts_with('.') && !first_word.starts_with('/') && !first_word.contains('/') {
        let dot_pos = first_word.find('.').unwrap();
        let after_dot = &first_word[dot_pos + 1..];
        if after_dot.starts_with(|c: char| c.is_alphabetic()) {
            return true;
        }
    }

    // String interpolation
    if trimmed.contains("#{") {
        return true;
    }

    // Array/hash literals at start
    if trimmed.starts_with('[') || trimmed.starts_with('{') {
        return true;
    }

    // true/false/nil as standalone
    if ["true", "false", "nil"].iter().any(|k| k.eq_ignore_ascii_case(first_word)) {
        return true;
    }

    // NOT a Rush syntax — likely a shell command
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rush_keywords() {
        assert!(is_rush_syntax("if x > 5"));
        assert!(is_rush_syntax("for x in items"));
        assert!(is_rush_syntax("while true"));
        assert!(is_rush_syntax("def greet(name)"));
        assert!(is_rush_syntax("class Dog"));
        assert!(is_rush_syntax("end"));
        assert!(is_rush_syntax("return 42"));
        assert!(is_rush_syntax("break"));
        assert!(is_rush_syntax("next if done"));
    }

    #[test]
    fn rush_builtins() {
        assert!(is_rush_syntax("puts \"hello\""));
        assert!(is_rush_syntax("print x"));
        assert!(is_rush_syntax("warn \"error\""));
        assert!(is_rush_syntax("exit 1"));
    }

    #[test]
    fn rush_assignments() {
        assert!(is_rush_syntax("x = 42"));
        assert!(is_rush_syntax("a, b = 1, 2"));
        assert!(is_rush_syntax("x += 1"));
        assert!(is_rush_syntax("name = \"rush\""));
    }

    #[test]
    fn rush_method_calls() {
        assert!(is_rush_syntax("greet(\"hello\")"));
        assert!(is_rush_syntax("items.each { |x| puts x }"));
        assert!(is_rush_syntax("File.read(\"path\")"));
        assert!(is_rush_syntax("x.upcase"));
    }

    #[test]
    fn rush_interpolation() {
        assert!(is_rush_syntax("puts \"hello #{name}\""));
    }

    #[test]
    fn shell_commands() {
        assert!(!is_rush_syntax("ls -la"));
        assert!(!is_rush_syntax("grep foo bar.txt"));
        assert!(!is_rush_syntax("mkdir -p /tmp/test"));
        assert!(!is_rush_syntax("cat /etc/hosts"));
        assert!(!is_rush_syntax("git status"));
        assert!(!is_rush_syntax("docker ps"));
        assert!(!is_rush_syntax("ssh user@host"));
    }

    #[test]
    fn ps_disambiguation() {
        // Bare "ps" = Rush block keyword
        assert!(is_rush_syntax("ps"));
        // "ps aux" = shell command
        assert!(!is_rush_syntax("ps aux"));
        assert!(!is_rush_syntax("ps -ef"));
    }

    #[test]
    fn cd_is_shell() {
        // cd is a shell builtin, not Rush syntax
        assert!(!is_rush_syntax("cd /tmp"));
        assert!(!is_rush_syntax("cd ~"));
    }
}
