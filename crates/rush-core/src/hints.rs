//! Training hints — contextual suggestions after commands.
//! Two modes:
//!   1. Error hints: suggest fixes after failed commands (typos, missing tools)
//!   2. Training hints: suggest Rush alternatives after successful bash-isms

use std::collections::HashMap;
use std::sync::Mutex;

static IMPRESSIONS: Mutex<Option<HashMap<String, u32>>> = Mutex::new(None);
const MAX_IMPRESSIONS: u32 = 3;

// ── Error hints (after failed commands) ────────────────────────────

/// Check if we should show a hint for a failed command.
pub fn hint_for_command(line: &str, exit_code: i32) -> Option<String> {
    let first_word = line.split_whitespace().next()?;

    // Command not found (127)
    if exit_code == 127 {
        // For pipelines, blame the actual missing token rather than
        // the first word. If any segment's first word is a rush-cli
        // builtin or Rush keyword, that's almost certainly what
        // failed (the native pipeline can't resolve it on PATH).
        if line.contains('|') {
            for seg in line.split('|') {
                let word = seg.split_whitespace().next().unwrap_or("");
                if is_rush_internal(word) {
                    return Some(format!(
                        "hint: '{word}' is a rush builtin and cannot be used as a pipeline stage yet"
                    ));
                }
            }
        }
        return hint_not_found(first_word);
    }

    // Permission denied (126)
    if exit_code == 126 {
        return Some(format!("hint: try 'chmod +x {first_word}' or 'sudo {line}'"));
    }

    // Bash-isms that cause errors
    hint_bashism_error(line)
}

/// Names that rush handles in-process (cli builtins) or as Rush
/// language keywords. None of these resolve via PATH, so when a 127
/// is produced by a pipeline containing one, this is almost always
/// the segment that failed.
fn is_rush_internal(word: &str) -> bool {
    matches!(
        word,
        "ai" | "alias" | "unalias" | "history" | "path" | "set" | "setbg"
        | "help" | "source" | "reload" | "sync" | "sql" | "init"
        | "puts" | "print" | "where" | "sort" | "first" | "last"
        | "columns" | "distinct" | "count" | "select"
    )
}

fn hint_not_found(cmd: &str) -> Option<String> {
    match cmd {
        "claer" | "cler" => Some("hint: did you mean 'clear'?".into()),
        "gti" | "got" => Some("hint: did you mean 'git'?".into()),
        "sl" => Some("hint: did you mean 'ls'?".into()),
        "suod" | "sduo" => Some("hint: did you mean 'sudo'?".into()),
        "apt" | "apt-get" if cfg!(target_os = "macos") => {
            Some("hint: macOS uses 'brew install' instead of apt".into())
        }
        "yum" | "dnf" if cfg!(target_os = "macos") => {
            Some("hint: macOS uses 'brew install' instead of yum/dnf".into())
        }
        "brew" if cfg!(target_os = "linux") => {
            Some("hint: try 'apt install' or 'dnf install' on Linux".into())
        }
        "python" => Some("hint: try 'python3' — Python 2 is often not installed".into()),
        "pip" => Some("hint: try 'pip3' or 'python3 -m pip'".into()),
        _ => {
            if cmd.contains('.') {
                Some(format!("hint: '{cmd}' — try assigning to a variable first: x = ...; x.method()"))
            } else {
                Some(format!("hint: '{cmd}' not found — check 'path check' or 'which {cmd}'"))
            }
        }
    }
}

fn hint_bashism_error(line: &str) -> Option<String> {
    if line.trim() == "fi" || line.trim() == "done" || line.trim() == "esac" {
        return Some("hint: Rush uses 'end' instead of 'fi', 'done', or 'esac'".into());
    }
    if line.trim() == "then" {
        return Some("hint: Rush doesn't use 'then' — just put the body after the condition".into());
    }
    if line.contains("[ -f ") || line.contains("test -f ") {
        return Some("hint: in Rush, use File.exist?(\"path\") instead of [ -f path ]".into());
    }
    if line.contains("[ -d ") || line.contains("test -d ") {
        return Some("hint: in Rush, use Dir.exist?(\"path\") instead of [ -d path ]".into());
    }
    None
}

// ── Training hints (after successful commands) ─────────────────────

/// Check a successfully executed command for bash patterns and suggest
/// Rush alternatives. Shows each hint up to MAX_IMPRESSIONS times.
pub fn training_hint(command: &str) -> Option<String> {
    let (key, suggestion) = match_training_pattern(command)?;

    // Frequency cap
    let mut guard = IMPRESSIONS.lock().unwrap();
    let map = guard.get_or_insert_with(HashMap::new);
    let count = map.entry(key.to_string()).or_insert(0);
    if *count >= MAX_IMPRESSIONS {
        return None;
    }
    *count += 1;

    Some(format!("~ Rush: {suggestion}"))
}

fn match_training_pattern(cmd: &str) -> Option<(&'static str, String)> {
    let cmd = cmd.trim();

    // find . -name "*.ext" → Dir.list(".", :recurse) | where /\.ext$/
    if cmd.starts_with("find ") && (cmd.contains("-name") || cmd.contains("-iname")) {
        let parts: Vec<&str> = cmd.split_whitespace().collect();
        let dir = parts.get(1).unwrap_or(&".");
        if let Some(pos) = parts.iter().position(|p| *p == "-name" || *p == "-iname") {
            if let Some(pat) = parts.get(pos + 1) {
                let pat = pat.trim_matches('"').trim_matches('\'');
                if let Some(ext) = pat.strip_prefix("*.") {
                    return Some(("find-name", format!("Dir.list(\"{dir}\", :recurse) | where /\\.{ext}$/")));
                }
            }
        }
    }

    // find . -type f → Dir.list(".", :recurse, :files)
    if cmd.starts_with("find ") && cmd.contains("-type f") {
        let dir = cmd.split_whitespace().nth(1).unwrap_or(".");
        return Some(("find-type-f", format!("Dir.list(\"{dir}\", :recurse, :files)")));
    }

    // find . -type d → Dir.list(".", :recurse, :dirs)
    if cmd.starts_with("find ") && cmd.contains("-type d") {
        let dir = cmd.split_whitespace().nth(1).unwrap_or(".");
        return Some(("find-type-d", format!("Dir.list(\"{dir}\", :recurse, :dirs)")));
    }

    // cat file | grep pattern → File.read_lines("file") | where /pattern/
    if cmd.starts_with("cat ") && cmd.contains("| grep") {
        let parts: Vec<&str> = cmd.splitn(2, '|').collect();
        let file = parts[0].trim().strip_prefix("cat ").unwrap_or("").trim();
        let pat = parts.get(1).map(|s| s.trim().strip_prefix("grep").unwrap_or("").trim()).unwrap_or("");
        let pat = pat.trim_matches('"').trim_matches('\'');
        if !file.is_empty() && !pat.is_empty() {
            return Some(("cat-grep", format!("File.read_lines(\"{file}\") | where /{pat}/")));
        }
    }

    // cat file | wc -l → File.read_lines("file").count
    if cmd.starts_with("cat ") && cmd.contains("| wc -l") {
        let file = cmd.split('|').next().unwrap_or("").trim().strip_prefix("cat ").unwrap_or("").trim();
        if !file.is_empty() {
            return Some(("cat-wc", format!("File.read_lines(\"{file}\").count")));
        }
    }

    // cat file (bare) → File.read("file")
    if cmd.starts_with("cat ") && !cmd.contains('|') {
        let file = cmd.strip_prefix("cat ").unwrap_or("").trim();
        if !file.is_empty() && !file.starts_with('-') {
            return Some(("cat-bare", format!("File.read(\"{file}\")")));
        }
    }

    // echo $VAR → puts varname
    if cmd.starts_with("echo $") && !cmd.contains('|') {
        let var = cmd.strip_prefix("echo $").unwrap_or("").trim();
        if !var.is_empty() && var.chars().all(|c| c.is_alphanumeric() || c == '_') {
            return Some(("echo-var", format!("puts {var}  (no $ needed in Rush)")));
        }
    }

    // sort | uniq → | distinct
    if cmd.contains("| sort") && cmd.contains("| uniq") {
        return Some(("sort-uniq", "| distinct  (works on unsorted data too)".into()));
    }

    // | head N → | first N
    if cmd.contains("| head") {
        if let Some(n) = extract_number_after(cmd, "head") {
            return Some(("pipe-head", format!("| first {n}  (Rush pipeline operator)")));
        }
    }

    // | tail N → | last N
    if cmd.contains("| tail") {
        if let Some(n) = extract_number_after(cmd, "tail") {
            return Some(("pipe-tail", format!("| last {n}  (Rush pipeline operator)")));
        }
    }

    // awk '{print $N}' → | columns N
    if cmd.contains("awk") && cmd.contains("print $") {
        return Some(("awk-print", "| columns N  (Rush column selector)".into()));
    }

    // sed 's/.../.../g' → .replace or .gsub
    if cmd.contains("sed") && cmd.contains("s/") {
        return Some(("sed-sub", ".replace(\"old\", \"new\")  or  .gsub(/pattern/, \"new\")".into()));
    }

    // | xargs → | each
    if cmd.contains("| xargs") {
        return Some(("xargs", "| each { |item| command item }".into()));
    }

    // grep -c → | where /pat/ | count
    if cmd.contains("grep -c") {
        return Some(("grep-c", "| where /pattern/ | count".into()));
    }

    // grep -r pattern dir → Dir.list(dir, :recurse) | where /pattern/
    if cmd.starts_with("grep -r") || cmd.starts_with("grep -R") {
        return Some(("grep-r", "Dir.list(\"dir\", :recurse) | where /pattern/".into()));
    }

    // head -N file → File.read_lines("file").first(N)
    if cmd.starts_with("head ") && !cmd.contains('|') {
        if let (Some(n), Some(file)) = (extract_number_after(cmd, "head"), cmd.split_whitespace().last()) {
            if !file.starts_with('-') {
                return Some(("head-file", format!("File.read_lines(\"{file}\").first({n})")));
            }
        }
    }

    // tail -N file → File.read_lines("file").last(N)
    if cmd.starts_with("tail ") && !cmd.contains('|') {
        if let (Some(n), Some(file)) = (extract_number_after(cmd, "tail"), cmd.split_whitespace().last()) {
            if !file.starts_with('-') {
                return Some(("tail-file", format!("File.read_lines(\"{file}\").last({n})")));
            }
        }
    }

    // cut -d → .split
    if cmd.contains("cut -d") {
        return Some(("cut", ".split(\"delim\")[N]  (Rush string split)".into()));
    }

    None
}

fn extract_number_after(cmd: &str, keyword: &str) -> Option<u32> {
    let idx = cmd.find(keyword)?;
    let after = &cmd[idx + keyword.len()..];
    // Match -N or -n N
    let after = after.trim().trim_start_matches('-').trim_start_matches('n').trim();
    after.split_whitespace().next()?.parse().ok()
}
