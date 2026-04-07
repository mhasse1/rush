use reedline::{Completer, Span, Suggestion};

/// Tab completer for Rush — completes commands, files, directories, and Rush keywords.
pub struct RushCompleter {
    rush_keywords: Vec<String>,
}

impl RushCompleter {
    pub fn new() -> Self {
        let rush_keywords = vec![
            "if", "elsif", "else", "end", "for", "in", "while", "until", "unless",
            "loop", "def", "return", "class", "attr", "enum", "case", "when",
            "try", "rescue", "ensure", "begin", "break", "next", "continue",
            "true", "false", "nil", "and", "or", "not", "puts", "print", "warn",
            "File", "Dir", "Time", "env",
        ]
        .into_iter()
        .map(String::from)
        .collect();

        Self { rush_keywords }
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
        let looks_like_path = partial.contains('/') || partial.starts_with('~') || partial.starts_with('.');

        if looks_like_path || !is_first_word {
            suggestions.extend(complete_path(partial, span));
        }

        if is_first_word {
            suggestions.extend(complete_commands(partial, span));
        }

        // Rush keywords
        for kw in &self.rush_keywords {
            if kw.starts_with(partial) || kw.to_lowercase().starts_with(&partial.to_lowercase()) {
                suggestions.push(suggestion(kw.clone(), span, true));
            }
        }

        suggestions
    }
}

fn complete_path(partial: &str, span: Span) -> Vec<Suggestion> {
    let mut suggestions = Vec::new();

    let expanded = if let Some(rest) = partial.strip_prefix("~/") {
        let home = std::env::var("HOME").unwrap_or_default();
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
                let is_dir = entry.file_type().map(|t| t.is_dir()).unwrap_or(false);
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
