//! History storage and Up/Down navigation cursor.
//!
//! ## Model
//!
//! History is a deduplicated list of submitted lines, oldest first.
//! Navigation is a stateful cursor that walks backward (older) and
//! forward (newer) through the list. While navigating, the
//! [`History::current`] returns the entry the cursor points at; once
//! the user pushes past the newest entry, `current` returns `None`
//! to mean "back at the live editing buffer."
//!
//! The engine stashes the user's in-progress edit on the first
//! [`History::backward`] of a session, so [`History::forward`] past
//! the newest entry can restore it. Stash management lives on the
//! engine side because the buffer is the engine's data; this module
//! only provides the read cursor over committed entries.
//!
//! ## Persistence
//!
//! [`FileBackedHistory`] reads and writes a plain-text file (one
//! entry per line, oldest first) — the same format `bash` uses for
//! `~/.bash_history` and `rushline`'s `FileBackedHistory` ships with.
//! Entries are appended on [`History::sync`]. Loading on construction
//! drops anything beyond `capacity`.
//!
//! ## Conventions
//!
//! - Empty submissions are not added.
//! - A submission identical to the most recent entry is not added
//!   (consecutive dedup, matching bash's `HISTCONTROL=ignoredups`).
//! - Submissions whose first byte is a space are not added — the
//!   "leading-space hides from history" convention. Useful for typing
//!   `  rm -rf /tmp/scratch` without it cluttering recall.

use std::fs::{File, OpenOptions};
use std::io::{self, BufRead, BufReader, BufWriter, Write};
use std::path::PathBuf;

pub trait History {
    /// Add `line` to history if it passes the dedup/empty/leading-
    /// space filters. Resets the navigation cursor to "present" (no
    /// recalled entry).
    fn add(&mut self, line: &str);

    /// Move cursor to an older entry. Returns the new current entry,
    /// or `None` if there's nothing older.
    fn backward(&mut self) -> Option<&str>;

    /// Move cursor to a newer entry, eventually returning to "present"
    /// (no entry recalled). Returns the new current entry, or `None`
    /// if at present.
    fn forward(&mut self) -> Option<&str>;

    /// `true` when the cursor is at the live editing buffer (no entry
    /// recalled). Used by the engine to decide whether the first
    /// `backward` should stash the in-progress edit.
    fn at_present(&self) -> bool;

    /// Reset the cursor without modifying entries. Call when the user
    /// edits the buffer in a way that should detach from a recalled
    /// entry (e.g. typing).
    fn reset_cursor(&mut self);

    /// Persist any new entries that haven't yet been written.
    fn sync(&mut self) -> io::Result<()>;

    /// All entries, oldest first. Used by the autosuggestion hint
    /// (which scans from newest backward looking for a prefix match)
    /// and could be used by future history-search features.
    fn entries(&self) -> &[String];
}

#[derive(Debug)]
pub struct FileBackedHistory {
    entries: Vec<String>,
    /// Read cursor for navigation. `None` means "at present" (the live
    /// buffer the user is editing). `Some(i)` means "looking at
    /// `entries[i]`" — older entries have smaller `i`.
    cursor: Option<usize>,
    capacity: usize,
    path: Option<PathBuf>,
    /// Index of the first entry that hasn't been flushed to disk yet.
    /// `entries.len()` means "all entries are persisted."
    dirty_from: usize,
}

impl FileBackedHistory {
    /// In-memory only history. `add`/`backward`/`forward` work; `sync`
    /// is a no-op. For tests and the demo binary.
    pub fn in_memory(capacity: usize) -> Self {
        Self {
            entries: Vec::new(),
            cursor: None,
            capacity,
            path: None,
            dirty_from: 0,
        }
    }

    /// Load history from `path`, keeping at most `capacity` entries
    /// (the most recent are kept if the file exceeds it). Creates the
    /// parent directory if missing. A non-existent path is fine —
    /// history starts empty.
    pub fn with_file(capacity: usize, path: PathBuf) -> io::Result<Self> {
        let mut entries = Vec::new();
        if let Ok(file) = File::open(&path) {
            let reader = BufReader::new(file);
            for line in reader.lines() {
                let line = line?;
                if !line.is_empty() {
                    entries.push(line);
                }
            }
            // Trim to capacity, keeping the most recent.
            if entries.len() > capacity {
                let drop = entries.len() - capacity;
                entries.drain(..drop);
            }
        }
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let dirty_from = entries.len();
        Ok(Self {
            entries,
            cursor: None,
            capacity,
            path: Some(path),
            dirty_from,
        })
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Returns `true` if the cursor is at "present" (no entry recalled).
    pub fn at_present(&self) -> bool {
        self.cursor.is_none()
    }

    /// Returns the entry the cursor currently points at, or `None`
    /// if at present.
    pub fn current(&self) -> Option<&str> {
        self.cursor.and_then(|i| self.entries.get(i).map(String::as_str))
    }

    fn should_save(&self, line: &str) -> bool {
        if line.is_empty() {
            return false;
        }
        if line.starts_with(' ') {
            return false;
        }
        if self.entries.last().map(String::as_str) == Some(line) {
            return false;
        }
        true
    }

    fn enforce_capacity(&mut self) {
        if self.capacity == 0 {
            self.entries.clear();
            self.dirty_from = 0;
            return;
        }
        if self.entries.len() > self.capacity {
            let drop = self.entries.len() - self.capacity;
            self.entries.drain(..drop);
            self.dirty_from = self.dirty_from.saturating_sub(drop);
        }
    }
}

impl History for FileBackedHistory {
    fn add(&mut self, line: &str) {
        self.cursor = None;
        if !self.should_save(line) {
            return;
        }
        self.entries.push(line.to_string());
        self.enforce_capacity();
    }

    fn at_present(&self) -> bool {
        FileBackedHistory::at_present(self)
    }

    fn entries(&self) -> &[String] {
        &self.entries
    }

    fn backward(&mut self) -> Option<&str> {
        let new = match self.cursor {
            None => self.entries.len().checked_sub(1)?,
            Some(0) => return self.entries.first().map(String::as_str), // can't go further
            Some(i) => i - 1,
        };
        self.cursor = Some(new);
        self.entries.get(new).map(String::as_str)
    }

    fn forward(&mut self) -> Option<&str> {
        let cur = self.cursor?;
        if cur + 1 >= self.entries.len() {
            self.cursor = None;
            return None;
        }
        self.cursor = Some(cur + 1);
        self.entries.get(cur + 1).map(String::as_str)
    }

    fn reset_cursor(&mut self) {
        self.cursor = None;
    }

    fn sync(&mut self) -> io::Result<()> {
        let Some(path) = self.path.clone() else {
            return Ok(());
        };
        if self.dirty_from >= self.entries.len() {
            return Ok(());
        }
        // Append the new entries to the on-disk file. We'd ideally
        // detect if another instance trimmed the file underneath us,
        // but for now we just append — bash with histappend has the
        // same behavior, which the user is already used to.
        let file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)?;
        let mut writer = BufWriter::new(file);
        for entry in &self.entries[self.dirty_from..] {
            writeln!(writer, "{entry}")?;
        }
        writer.flush()?;
        self.dirty_from = self.entries.len();
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_history_returns_none_on_navigation() {
        let mut h = FileBackedHistory::in_memory(100);
        assert_eq!(h.backward(), None);
        assert_eq!(h.forward(), None);
        assert!(h.at_present());
    }

    #[test]
    fn add_then_navigate() {
        let mut h = FileBackedHistory::in_memory(100);
        h.add("one");
        h.add("two");
        h.add("three");
        // Up walks oldest-direction starting from the most recent.
        assert_eq!(h.backward(), Some("three"));
        assert_eq!(h.backward(), Some("two"));
        assert_eq!(h.backward(), Some("one"));
        // Past the oldest stays at the oldest.
        assert_eq!(h.backward(), Some("one"));
        // Down walks newest-direction.
        assert_eq!(h.forward(), Some("two"));
        assert_eq!(h.forward(), Some("three"));
        // Past the newest returns to "present" (None).
        assert_eq!(h.forward(), None);
        assert!(h.at_present());
    }

    #[test]
    fn empty_lines_are_not_saved() {
        let mut h = FileBackedHistory::in_memory(100);
        h.add("");
        assert_eq!(h.len(), 0);
    }

    #[test]
    fn leading_space_lines_are_not_saved() {
        let mut h = FileBackedHistory::in_memory(100);
        h.add(" rm -rf hidden");
        assert_eq!(h.len(), 0);
    }

    #[test]
    fn consecutive_duplicates_are_deduped() {
        let mut h = FileBackedHistory::in_memory(100);
        h.add("ls");
        h.add("ls");
        h.add("ls");
        assert_eq!(h.len(), 1);
        h.add("cd");
        h.add("ls"); // not consecutive with last "ls", saved
        assert_eq!(h.len(), 3);
    }

    #[test]
    fn add_resets_navigation_cursor() {
        let mut h = FileBackedHistory::in_memory(100);
        h.add("one");
        h.add("two");
        h.backward(); // now on "two"
        h.backward(); // now on "one"
        assert_eq!(h.current(), Some("one"));
        h.add("three"); // submit a new entry — cursor should reset
        assert!(h.at_present());
    }

    #[test]
    fn capacity_keeps_most_recent() {
        let mut h = FileBackedHistory::in_memory(2);
        h.add("a");
        h.add("b");
        h.add("c");
        assert_eq!(h.len(), 2);
        // "a" was dropped; cursor at present.
        assert_eq!(h.backward(), Some("c"));
        assert_eq!(h.backward(), Some("b"));
        assert_eq!(h.backward(), Some("b")); // floor
    }

    #[test]
    fn reset_cursor_returns_to_present() {
        let mut h = FileBackedHistory::in_memory(100);
        h.add("one");
        h.backward();
        assert_eq!(h.current(), Some("one"));
        h.reset_cursor();
        assert!(h.at_present());
    }

    #[test]
    fn file_persistence_roundtrip() {
        let dir = std::env::temp_dir().join(format!("rush-line-history-test-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let path = dir.join("history");
        let _ = std::fs::remove_file(&path);

        {
            let mut h = FileBackedHistory::with_file(100, path.clone()).unwrap();
            h.add("one");
            h.add("two");
            h.add("three");
            h.sync().unwrap();
        }

        let h2 = FileBackedHistory::with_file(100, path.clone()).unwrap();
        assert_eq!(h2.len(), 3);
        assert_eq!(h2.entries, vec!["one", "two", "three"]);

        // Append more, sync only writes new ones (dirty_from tracks).
        {
            let mut h3 = FileBackedHistory::with_file(100, path.clone()).unwrap();
            h3.add("four");
            h3.sync().unwrap();
        }

        let h4 = FileBackedHistory::with_file(100, path.clone()).unwrap();
        assert_eq!(h4.entries, vec!["one", "two", "three", "four"]);

        let _ = std::fs::remove_file(&path);
        let _ = std::fs::remove_dir(&dir);
    }

    #[test]
    fn file_load_trims_to_capacity_keeping_most_recent() {
        let dir = std::env::temp_dir().join(format!("rush-line-history-trim-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let path = dir.join("history");

        // Pre-populate with 5 entries.
        std::fs::write(&path, "a\nb\nc\nd\ne\n").unwrap();

        let h = FileBackedHistory::with_file(3, path.clone()).unwrap();
        assert_eq!(h.entries, vec!["c", "d", "e"]);

        let _ = std::fs::remove_file(&path);
        let _ = std::fs::remove_dir(&dir);
    }
}
