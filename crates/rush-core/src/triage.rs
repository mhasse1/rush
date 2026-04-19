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
        "if", "elsif", "else", "unless", "while", "until", "for",
        "parallel", "parallel!", "orchestrate", "loop",
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
                    // Inline env var prefix (POSIX 'VAR=val cmd args...'):
                    // first word has '=' with no space around it AND there's
                    // at least one more whitespace-separated word after.
                    // That's a shell invocation, not a rush assignment.
                    let is_inline_env_prefix = !first_word.is_empty()
                        && first_word.contains('=')
                        && !first_word.starts_with('=')
                        && trimmed[first_word.len()..]
                            .trim_start()
                            .chars().next()
                            .is_some();
                    if is_ident && !is_inline_env_prefix {
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

    // Stdlib receiver: File.xxx, Dir.xxx, Time.xxx, Path.xxx, env.xxx
    let stdlib = ["file", "dir", "time", "env", "path"];
    if stdlib.iter().any(|s| first_word.eq_ignore_ascii_case(s)) && first_word.contains('.') || trimmed.starts_with("File.") || trimmed.starts_with("Dir.") || trimmed.starts_with("Time.") || trimmed.starts_with("Path.") || trimmed.starts_with("env.") {
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

    // Array/hash literals at start. Rush literals pack tight: `[1,2]`,
    // `{a:1}`. Shell `[ -f path ]` (the `test` bracket form) has a
    // mandatory space after the opener, so we exclude that.
    if trimmed.starts_with('[')
        && trimmed.chars().nth(1).is_some_and(|c| !c.is_whitespace())
    {
        return true;
    }
    if trimmed.starts_with('{') {
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

    #[test]
    fn sysadmin_edge_cases() {
        // --- Shell commands (should all return false) ---

        // AD distinguished names with = inside quoted args
        assert!(!is_rush_syntax(r#"dsquery user "CN=John,OU=Users,DC=corp,DC=local""#));

        // net commands (Windows admin)
        assert!(!is_rush_syntax(r#"net stop "remoteaccess""#));
        assert!(!is_rush_syntax("net user admin /add"));
        assert!(!is_rush_syntax(r#"net group "Domain Admins" /domain"#));

        // Windows commands with = in args
        assert!(!is_rush_syntax(r#"setx PATH "C:\bin;%PATH%""#));

        // UNC-style paths
        assert!(!is_rush_syntax(r#"ls "\\\\server\\share""#));

        // systemctl
        assert!(!is_rush_syntax("systemctl restart nginx"));
        assert!(!is_rush_syntax("systemctl status sshd"));

        // journalctl
        assert!(!is_rush_syntax("journalctl -u nginx --since today"));

        // iptables
        assert!(!is_rush_syntax("iptables -A INPUT -p tcp --dport 80 -j ACCEPT"));

        // chmod / chown
        assert!(!is_rush_syntax("chmod 755 /var/www"));
        assert!(!is_rush_syntax("chown -R www-data:www-data /var/www"));

        // rsync
        assert!(!is_rush_syntax("rsync -avz /src/ user@host:/dest/"));

        // curl with headers
        assert!(!is_rush_syntax(r#"curl -H "Authorization: Bearer token123" https://api.example.com"#));

        // Commands that LOOK like Rush but aren't (test builtin)
        assert!(!is_rush_syntax("test -f /etc/hosts"));

        // Commands with pipes that should stay as shell
        assert!(!is_rush_syntax("ps aux | grep nginx"));

        // --- Rush syntax (should all return true) ---

        // Assignment
        assert!(is_rush_syntax(r#"server = "cor1s04""#));

        // Stdlib method call
        assert!(is_rush_syntax(r#"File.exist?("/etc/hosts")"#));

        // Method chain with block
        assert!(is_rush_syntax("items.each { |x| puts x }"));

        // Control flow with stdlib
        assert!(is_rush_syntax(r#"if File.size("log") > 1mb"#));
    }

    // ── Systematic matrix ───────────────────────────────────────────
    //
    // Two tables — one per classification — exercised by rush_matrix
    // and shell_matrix below. New syntax / regressions only need a
    // single row added, and the failure message points at the exact
    // input so there's no guessing.

    /// Inputs that MUST classify as Rush syntax.
    const RUSH_CASES: &[&str] = &[
        // ── Block keywords ──
        "if x > 5",
        "elsif y",
        "else",
        "unless x",
        "while running",
        "until done",
        "for i in 1..10",
        "loop",
        "do |x|",
        "def greet(name)",
        "class Dog",
        "enum Color",
        "case x",
        "match x",
        "try",
        "begin",
        "parallel",
        "parallel!",
        "orchestrate",
        // ── Control flow terminators ──
        "end",
        "return",
        "return 42",
        "break",
        "break if done",
        "next",
        "next if skip",
        "continue",
        // ── Built-in functions (callable without parens) ──
        "puts x",
        "puts \"hello\"",
        "print x",
        "warn \"problem\"",
        "die \"fatal\"",
        "ask \"name?\"",
        "sleep 1",
        "exit",
        "exit 1",
        "ai \"question\"",
        // ── Assignments ──
        "x = 42",
        "name = \"rush\"",
        "a, b = 1, 2",
        "x, y, z = [1, 2, 3]",
        "server = \"host.example.com\"",
        "path = \"/tmp\"",
        // ── Compound assignments ──
        "x += 1",
        "x -= 1",
        "x *= 2",
        "x /= 2",
        // ── Function calls with parens ──
        "greet(\"world\")",
        "add(1, 2)",
        // ── Stdlib method calls ──
        "File.read(\"x.txt\")",
        "File.exist?(\"/etc/hosts\")",
        "Dir.list(\".\")",
        "Time.now",
        "Path.join(\"a\", \"b\")",
        "env.HOME",
        // ── Method chains ──
        "\"hello\".upcase",
        "x.upcase",
        "items.each { |x| puts x }",
        // ── Interpolation ──
        "puts \"hello #{name}\"",
        // ── Control-flow with stdlib call ──
        "if File.size(\"log\") > 1mb",
        // ── Platform keywords ──
        "macos",
        "linux",
        "win64",
        "win32",
        "isssh",
        "macos.version",
        // ── Plugins ──
        "plugin",
        "plugin.python",
        // ── Bare rush block keywords (no args) ──
        "ps",
        "ps5",
    ];

    /// Inputs that MUST classify as shell commands.
    const SHELL_CASES: &[&str] = &[
        // ── Basic commands ──
        "ls",
        "ls -la",
        "pwd",
        "whoami",
        "hostname",
        // ── Commands with args / flags ──
        "grep foo bar.txt",
        "cat /etc/hosts",
        "find . -name '*.rs'",
        "mkdir -p /tmp/test",
        "rm -rf /tmp/stale",
        "cp a b",
        "mv old new",
        // ── Shell builtins (handled in-process, not rush syntax) ──
        "cd /tmp",
        "cd ~",
        "cd -",
        "alias ll='ls -la'",
        "export FOO=bar",
        "unset FOO",
        "set -e",
        "printf \"%s\\n\" hi",
        "trap 'cleanup' EXIT",
        "wait",
        "exec bash",
        "read name",
        // ── Inline env var prefix (POSIX, #226) ──
        "FOO=hi alias myalias=\"echo test\"",
        "RUSH_AI_DEBUG=1 ai \"prompt\"",
        "VAR1=a VAR2=b cmd arg",
        // ── Pipes and redirects ──
        "ps aux | grep nginx",
        "cat f | wc -l",
        "echo hi > /tmp/out",
        "cmd 2>&1",
        // ── Version control / dev ──
        "git status",
        "git log --oneline",
        "docker ps",
        "docker run --rm -it alpine sh",
        "kubectl get pods",
        // ── System admin ──
        "systemctl restart nginx",
        "systemctl status sshd",
        "journalctl -u nginx --since today",
        "iptables -A INPUT -p tcp --dport 80 -j ACCEPT",
        "chmod 755 /var/www",
        "chown -R www-data:www-data /var/www",
        // ── Remote / transfer ──
        "ssh user@host",
        "scp a.txt host:/tmp/",
        "rsync -avz /src/ user@host:/dest/",
        // ── HTTP ──
        "curl -H \"Authorization: Bearer token\" https://api.example.com",
        "wget https://example.com/x.tar.gz",
        // ── Windows / paths with = ──
        "dsquery user \"CN=John,OU=Users,DC=corp,DC=local\"",
        "net stop \"remoteaccess\"",
        "net user admin /add",
        "setx PATH \"C:\\bin;%PATH%\"",
        "ls \"\\\\\\\\server\\\\share\"",
        // ── Test command (looks like rush but isn't) ──
        "test -f /etc/hosts",
        "[ -f /etc/hosts ]",
        // ── ps with args (only bare is rush) ──
        "ps aux",
        "ps -ef",
    ];

    #[test]
    fn rush_matrix() {
        let mut failures = Vec::new();
        for &case in RUSH_CASES {
            if !is_rush_syntax(case) {
                failures.push(case);
            }
        }
        assert!(
            failures.is_empty(),
            "expected these to classify as Rush:\n  {}",
            failures.join("\n  ")
        );
    }

    #[test]
    fn shell_matrix() {
        let mut failures = Vec::new();
        for &case in SHELL_CASES {
            if is_rush_syntax(case) {
                failures.push(case);
            }
        }
        assert!(
            failures.is_empty(),
            "expected these to classify as shell:\n  {}",
            failures.join("\n  ")
        );
    }
}
