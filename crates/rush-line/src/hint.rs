//! Autosuggestion hint — fish/zsh-style "what your buffer would
//! become if you completed it from history."
//!
//! The engine renders the suffix in dim color after the cursor
//! during a normal repaint. Right-arrow / End at end-of-buffer
//! accepts the hint by appending it to the buffer.
//!
//! This module is just the algorithm for picking which suffix to
//! render. The engine handles the wiring (rendering, acceptance,
//! invalidation on text-changing actions).

/// Most-recent history entry whose prefix matches `buffer`, returning
/// the part of that entry that *follows* the prefix. Empty string if
/// `buffer` is empty or no entry matches (or the only matches equal
/// the buffer exactly).
pub fn longest_history_match<'a, I>(buffer: &str, entries_oldest_first: I) -> String
where
    I: IntoIterator<Item = &'a str>,
    I::IntoIter: DoubleEndedIterator,
{
    if buffer.is_empty() {
        return String::new();
    }
    for entry in entries_oldest_first.into_iter().rev() {
        if entry.len() > buffer.len() && entry.starts_with(buffer) {
            return entry[buffer.len()..].to_string();
        }
    }
    String::new()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_buffer_no_hint() {
        let hist = vec!["ls", "cd"];
        assert_eq!(longest_history_match("", hist.iter().copied()), "");
    }

    #[test]
    fn matches_most_recent_match() {
        let hist = vec!["ls -la /tmp", "ls -la /home", "cd /tmp"];
        // Newest match wins: "ls -la /home"
        assert_eq!(longest_history_match("ls", hist.iter().copied()), " -la /home");
    }

    #[test]
    fn no_match_empty_hint() {
        let hist = vec!["ls", "cd"];
        assert_eq!(longest_history_match("xyz", hist.iter().copied()), "");
    }

    #[test]
    fn exact_match_returns_empty_hint() {
        // Don't suggest the buffer is already what the user typed.
        let hist = vec!["ls"];
        assert_eq!(longest_history_match("ls", hist.iter().copied()), "");
    }
}
