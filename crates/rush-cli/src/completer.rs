use reedline::{Completer, Span, Suggestion};

/// Tab completer for Rush — context-aware completions.
pub struct RushCompleter {
    rush_keywords: Vec<String>,
    builtins: Vec<String>,
    pipe_ops: Vec<String>,
    string_methods: Vec<String>,
    array_methods: Vec<String>,
    hash_methods: Vec<String>,
    numeric_methods: Vec<String>,
    stdlib_methods: Vec<String>,
}

impl RushCompleter {
    pub fn new() -> Self {
        Self {
            rush_keywords: vec![
                "if", "elsif", "else", "end", "for", "in", "while", "until", "unless",
                "loop", "def", "return", "class", "attr", "enum", "case", "when",
                "try", "rescue", "ensure", "begin", "break", "next", "continue",
                "true", "false", "nil", "and", "or", "not",
            ].into_iter().map(String::from).collect(),

            builtins: vec![
                "puts", "print", "warn", "die", "ask", "sleep", "exit",
                "cd", "export", "unset", "source", "alias", "unalias",
                "pushd", "popd", "dirs", "history", "path", "help", "set",
                "setbg", "reload", "init", "clear", "pwd", "which", "type",
                "command", "eval", "exec", "read", "trap", "jobs", "fg", "bg",
                "wait", "kill", "umask", "ulimit", "fc", "o", "ai", "sql", "sync",
                "File", "Dir", "Time", "Path", "env", "plugin",
            ].into_iter().map(String::from).collect(),

            pipe_ops: vec![
                "where", "select", "sort", "count", "first", "last", "skip",
                "sum", "avg", "min", "max", "distinct", "uniq", "reverse",
                "as json", "as csv", "from json", "from csv",
                "objectify", "grep", "head", "tail", "tee", "columns",
            ].into_iter().map(String::from).collect(),

            string_methods: vec![
                "upcase", "downcase", "strip", "lstrip", "rstrip", "trim",
                "split", "lines", "chars", "length", "size",
                "include?", "start_with?", "end_with?", "empty?",
                "replace", "gsub", "sub", "reverse",
                "to_i", "to_f", "to_s",
                "native_path", "unix_path",
            ].into_iter().map(String::from).collect(),

            array_methods: vec![
                "each", "map", "select", "reject", "sort", "sort_by",
                "first", "last", "length", "size", "count",
                "push", "pop", "shift", "unshift",
                "flatten", "uniq", "reverse", "join",
                "include?", "empty?", "any?", "all?",
                "sum", "min", "max", "reduce",
            ].into_iter().map(String::from).collect(),

            hash_methods: vec![
                "keys", "values", "length", "size", "empty?",
                "merge", "each",
            ].into_iter().map(String::from).collect(),

            numeric_methods: vec![
                "abs", "round", "even?", "odd?", "zero?",
                "positive?", "negative?",
                "times", "to_s", "to_f", "to_i",
                "hours", "minutes", "seconds", "days",
            ].into_iter().map(String::from).collect(),

            stdlib_methods: vec![
                // File.*
                "read", "write", "append", "exist?", "exists?", "delete",
                "copy", "move", "rename", "size", "basename", "dirname", "ext",
                "read_lines", "read_json",
                // Dir.*
                "list", "mkdir", "rmdir", "pwd", "home", "glob",
                // Time.*
                "now", "utc_now", "today", "epoch",
            ].into_iter().map(String::from).collect(),
        }
    }
}

fn suggestion(value: String, span: Span, append_whitespace: bool) -> Suggestion {
    Suggestion {
        value,
        display_override: None,
        description: None,
        style: None,
        extra: None,
        span,
        append_whitespace,
        match_indices: None,
    }
}

impl Completer for RushCompleter {
    fn complete(&mut self, line: &str, pos: usize) -> Vec<Suggestion> {
        let line_to_pos = &line[..pos];

        // Find the word being completed
        let word_start = line_to_pos
            .rfind(|c: char| c.is_whitespace())
            .map(|i| i + 1)
            .unwrap_or(0);
        let partial = &line_to_pos[word_start..];

        if partial.is_empty() {
            return Vec::new();
        }

        let span = Span::new(word_start, pos);
        let mut suggestions = Vec::new();

        let is_first_word = !line_to_pos[..word_start].contains(|c: char| !c.is_whitespace());
        let after_pipe = line_to_pos.rfind('|').map_or(false, |p| p > line_to_pos.rfind(|c: char| !c.is_whitespace() && c != '|').unwrap_or(0));
        let after_dot = word_start > 0 && line_to_pos.as_bytes().get(word_start - 1) == Some(&b'.');
        let looks_like_path = partial.contains('/') || partial.contains('\\') || partial.starts_with('~') || partial.starts_with('.');

        // 1. Dot-completion: variable.method
        if after_dot {
            let before_dot = &line_to_pos[..word_start - 1];
            let receiver = before_dot.split_whitespace().last().unwrap_or("");

            // Infer type from receiver name
            let methods = self.infer_methods(receiver, line);
            for m in &methods {
                if m.starts_with(partial) || m.to_lowercase().starts_with(&partial.to_lowercase()) {
                    suggestions.push(suggestion(m.clone(), span, false));
                }
            }
            return suggestions;
        }

        // 2. After pipe: pipeline operators
        if after_pipe {
            for op in &self.pipe_ops {
                if op.starts_with(partial) || op.to_lowercase().starts_with(&partial.to_lowercase()) {
                    suggestions.push(suggestion(op.clone(), span, true));
                }
            }
            // Also complete paths and commands after pipe
            suggestions.extend(complete_path(partial, span));
            suggestions.extend(complete_commands(partial, span));
            return suggestions;
        }

        // 3. Environment variable: $VAR
        if partial.starts_with('$') {
            let var_prefix = &partial[1..];
            for (key, _) in std::env::vars() {
                if key.starts_with(var_prefix) || key.to_lowercase().starts_with(&var_prefix.to_lowercase()) {
                    suggestions.push(suggestion(format!("${key}"), span, true));
                }
            }
            return suggestions;
        }

        // 4. Flags: -x after a command
        if partial.starts_with('-') && !is_first_word {
            // Common flags — could be command-specific in future
            return suggestions;
        }

        // 5. Paths
        if looks_like_path || (!is_first_word && !after_pipe) {
            suggestions.extend(complete_path(partial, span));
        }

        // 6. First word: commands + builtins + keywords
        if is_first_word {
            // Builtins
            for b in &self.builtins {
                if b.starts_with(partial) || b.to_lowercase().starts_with(&partial.to_lowercase()) {
                    suggestions.push(suggestion(b.clone(), span, true));
                }
            }
            // Rush keywords
            for kw in &self.rush_keywords {
                if kw.starts_with(partial) || kw.to_lowercase().starts_with(&partial.to_lowercase()) {
                    suggestions.push(suggestion(kw.clone(), span, true));
                }
            }
            // PATH commands
            suggestions.extend(complete_commands(partial, span));
        }

        // 7. Non-first, non-path: also try commands (for chained)
        if !is_first_word && !looks_like_path && suggestions.is_empty() {
            suggestions.extend(complete_path(partial, span));
        }

        suggestions
    }
}

impl RushCompleter {
    /// Infer available methods based on receiver context.
    fn infer_methods(&self, receiver: &str, line: &str) -> Vec<String> {
        // Stdlib receivers
        match receiver.to_lowercase().as_str() {
            "file" => return self.stdlib_methods.iter()
                .filter(|m| ["read", "write", "append", "exist?", "exists?", "delete",
                    "copy", "move", "rename", "size", "basename", "dirname", "ext",
                    "read_lines", "read_json"].contains(&m.as_str()))
                .cloned().collect(),
            "dir" => return self.stdlib_methods.iter()
                .filter(|m| ["list", "mkdir", "rmdir", "pwd", "home", "glob", "exist?", "exists?"]
                    .contains(&m.as_str()))
                .cloned().collect(),
            "time" => return self.stdlib_methods.iter()
                .filter(|m| ["now", "utc_now", "today", "epoch"].contains(&m.as_str()))
                .cloned().collect(),
            "path" => return vec![
                "sep", "join", "normalize", "native", "expand",
                "exist?", "absolute?", "basename", "dirname", "ext",
            ].into_iter().map(String::from).collect(),
            "plugin" => {
                // Complete plugin names from available plugins
                let mut names: Vec<String> = rush_core::plugin::list_available()
                    .into_iter().map(|(name, _)| name).collect();
                names.extend(["ps", "python", "node", "ruby"].iter().map(|s| s.to_string()));
                names.sort();
                names.dedup();
                return names;
            }
            _ => {}
        }

        // Try to infer from context: look for assignment patterns
        // x = "string" → string methods
        // x = [1,2,3] → array methods
        // x = {a: 1} → hash methods
        if let Some(assign_pos) = line.rfind(&format!("{receiver} = ")) {
            let after = &line[assign_pos + receiver.len() + 3..];
            let after = after.trim();
            if after.starts_with('"') || after.starts_with('\'') {
                return self.string_methods.clone();
            }
            if after.starts_with('[') {
                return self.array_methods.clone();
            }
            if after.starts_with('{') {
                return self.hash_methods.clone();
            }
            if after.parse::<f64>().is_ok() {
                return self.numeric_methods.clone();
            }
        }

        // Default: offer all common methods
        let mut all = Vec::new();
        all.extend(self.string_methods.iter().cloned());
        all.extend(self.array_methods.iter().take(10).cloned());
        all.sort();
        all.dedup();
        all
    }
}

fn complete_path(partial: &str, span: Span) -> Vec<Suggestion> {
    let mut suggestions = Vec::new();

    // Normalize backslashes to forward slashes (cross-platform)
    let partial = &partial.replace('\\', "/");

    let expanded = if let Some(rest) = partial.strip_prefix("~/") {
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_default()
            .replace('\\', "/");
        format!("{home}/{rest}")
    } else {
        partial.to_string()
    };

    let (dir, prefix) = if let Some(slash_pos) = expanded.rfind('/') {
        (&expanded[..=slash_pos], &expanded[slash_pos + 1..])
    } else {
        (".", expanded.as_str())
    };

    if let Ok(entries) = std::fs::read_dir(dir) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if name.starts_with(prefix) {
                // Use entry.path().is_dir() to follow symlinks (e.g., /home@ → directory)
                let is_dir = entry.path().is_dir();
                let full = if partial.contains('/') {
                    let base = &partial[..partial.rfind('/').unwrap() + 1];
                    format!("{base}{name}{}", if is_dir { "/" } else { "" })
                } else {
                    format!("{name}{}", if is_dir { "/" } else { "" })
                };
                suggestions.push(suggestion(full, span, !is_dir));
            }
        }
    }

    // Sort: directories first, then alphabetical (case-insensitive)
    suggestions.sort_by(|a, b| {
        let a_dir = a.value.ends_with('/');
        let b_dir = b.value.ends_with('/');
        match (a_dir, b_dir) {
            (true, false) => std::cmp::Ordering::Less,
            (false, true) => std::cmp::Ordering::Greater,
            _ => a.value.to_lowercase().cmp(&b.value.to_lowercase()),
        }
    });
    suggestions
}

fn complete_commands(partial: &str, span: Span) -> Vec<Suggestion> {
    let mut suggestions = Vec::new();
    let mut seen = std::collections::HashSet::new();

    let path_var = std::env::var("PATH").unwrap_or_default();
    let separator = if cfg!(windows) { ';' } else { ':' };

    for dir in path_var.split(separator) {
        if let Ok(entries) = std::fs::read_dir(dir) {
            for entry in entries.flatten() {
                let name = entry.file_name().to_string_lossy().to_string();
                if name.starts_with(partial) && !seen.contains(&name) {
                    #[cfg(unix)]
                    {
                        use std::os::unix::fs::PermissionsExt;
                        if let Ok(meta) = entry.metadata() {
                            if meta.permissions().mode() & 0o111 == 0 {
                                continue;
                            }
                        }
                    }
                    seen.insert(name.clone());
                    suggestions.push(suggestion(name, span, true));
                }
            }
        }
    }

    suggestions.sort_by(|a, b| a.value.cmp(&b.value));
    suggestions
}
