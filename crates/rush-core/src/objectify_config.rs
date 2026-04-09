//! Objectify configuration — per-command parse hints for text→object conversion.
//!
//! Three-layer config (later layers override earlier):
//!   1. Built-in defaults (compiled in)
//!   2. System config: /etc/rush/objectify.yaml
//!   3. User config:   ~/.config/rush/objectify.yaml
//!
//! Format (YAML):
//! ```yaml
//! ps:
//!   # default: whitespace split, last column joins remaining
//!
//! docker ps:
//!   delim: '\s{2,}'
//!
//! free:
//!   skip: 1
//!
//! netstat:
//!   fixed: true
//!
//! lsblk:
//!   cols: [NAME, SIZE, TYPE, MOUNTPOINT]
//! ```

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::Mutex;

/// Parse hints for a single command.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ObjectifyHint {
    /// Custom delimiter regex (default: whitespace)
    #[serde(default)]
    pub delim: Option<String>,

    /// Fixed-width column parsing (position-based)
    #[serde(default)]
    pub fixed: bool,

    /// Skip N data lines after header
    #[serde(default)]
    pub skip: usize,

    /// Explicit column names (for commands without headers)
    #[serde(default)]
    pub cols: Option<Vec<String>>,

    /// Which line is the header (0-indexed, default 0)
    #[serde(default)]
    pub header: usize,
}

/// The full objectify configuration.
#[derive(Debug, Clone)]
pub struct ObjectifyConfig {
    commands: HashMap<String, ObjectifyHint>,
}

// ── Global singleton ───────────────────────────────────────────────

static CONFIG: Mutex<Option<ObjectifyConfig>> = Mutex::new(None);

/// Get the global objectify config, loading on first access.
pub fn get() -> ObjectifyConfig {
    let mut guard = CONFIG.lock().unwrap();
    if guard.is_none() {
        *guard = Some(ObjectifyConfig::load());
    }
    guard.clone().unwrap()
}

/// Reload the config (after user edits the file).
pub fn reload() {
    let mut guard = CONFIG.lock().unwrap();
    *guard = Some(ObjectifyConfig::load());
}

// ── Built-in defaults ──────────────────────────────────────────────

fn built_in_defaults() -> HashMap<String, ObjectifyHint> {
    let mut m = HashMap::new();
    let default = || ObjectifyHint::default();

    // Standard Unix commands with clean tabular output
    m.insert("ps".into(), default());
    m.insert("df".into(), default());
    m.insert("w".into(), default());
    m.insert("who".into(), default());
    m.insert("last".into(), default());
    m.insert("ss".into(), default());
    m.insert("mount".into(), default());
    m.insert("ip".into(), default());
    m.insert("ifconfig".into(), default());
    m.insert("lsblk".into(), default());
    m.insert("blkid".into(), default());

    // Commands needing special handling
    m.insert("netstat".into(), ObjectifyHint { fixed: true, ..default() });
    m.insert("lsof".into(), ObjectifyHint { fixed: true, ..default() });
    m.insert("free".into(), ObjectifyHint { skip: 1, ..default() });

    // Docker uses 2+ space delimiters (columns contain single spaces)
    m.insert("docker ps".into(), ObjectifyHint {
        delim: Some(r"\s{2,}".into()), ..default()
    });
    m.insert("docker images".into(), ObjectifyHint {
        delim: Some(r"\s{2,}".into()), ..default()
    });

    // Kubernetes
    m.insert("kubectl get".into(), default());

    m
}

// ── Config loading ─────────────────────────────────────────────────

impl ObjectifyConfig {
    fn load() -> Self {
        let mut commands = built_in_defaults();

        // Layer 2: system config
        let system_path = if cfg!(windows) {
            std::env::var("PROGRAMDATA")
                .map(|p| format!("{p}/rush/objectify.yaml"))
                .unwrap_or_else(|_| "C:/ProgramData/rush/objectify.yaml".into())
        } else {
            "/etc/rush/objectify.yaml".into()
        };
        load_yaml_file(&system_path, &mut commands);

        // Layer 3: user config
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_default();
        let user_path = format!("{home}/.config/rush/objectify.yaml");
        load_yaml_file(&user_path, &mut commands);

        ObjectifyConfig { commands }
    }

    /// Look up hints for a command line.
    /// Tries 2-word match first ("docker ps"), then 1-word ("netstat").
    pub fn get_hint(&self, command_line: &str) -> Option<&ObjectifyHint> {
        let parts: Vec<&str> = command_line.split_whitespace().collect();

        // Try 2-word match: "docker ps" from "docker ps -a"
        if parts.len() >= 2 {
            let two_word = format!("{} {}", parts[0], parts[1]);
            if let Some(hint) = self.commands.get(&two_word) {
                return Some(hint);
            }
        }

        // Try 1-word match
        if let Some(first) = parts.first() {
            if let Some(hint) = self.commands.get(*first) {
                return Some(hint);
            }
        }

        None
    }

    /// Check if a command should be auto-objectified.
    pub fn should_objectify(&self, command_line: &str) -> bool {
        self.get_hint(command_line).is_some()
    }

    /// Get all configured command names (for tab completion).
    pub fn command_names(&self) -> Vec<String> {
        let mut names: Vec<String> = self.commands.keys().cloned().collect();
        names.sort();
        names
    }
}

fn load_yaml_file(path: &str, commands: &mut HashMap<String, ObjectifyHint>) {
    let content = match std::fs::read_to_string(path) {
        Ok(c) => c,
        Err(_) => return,
    };

    match serde_yaml::from_str::<HashMap<String, Option<ObjectifyHint>>>(&content) {
        Ok(parsed) => {
            for (cmd, hint) in parsed {
                commands.insert(cmd, hint.unwrap_or_default());
            }
        }
        Err(e) => {
            eprintln!("rush: warning: failed to parse {path}: {e}");
        }
    }
}

// ── Tests ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn built_in_has_ps() {
        let config = ObjectifyConfig::load();
        assert!(config.should_objectify("ps aux"));
        assert!(config.should_objectify("ps -ef"));
        assert!(config.should_objectify("ps"));
    }

    #[test]
    fn built_in_has_df() {
        let config = ObjectifyConfig::load();
        assert!(config.should_objectify("df -h"));
        assert!(config.should_objectify("df"));
    }

    #[test]
    fn two_word_match() {
        let config = ObjectifyConfig::load();
        assert!(config.should_objectify("docker ps -a"));
        assert!(config.should_objectify("docker images"));
        assert!(config.should_objectify("kubectl get pods"));
    }

    #[test]
    fn unknown_command() {
        let config = ObjectifyConfig::load();
        assert!(!config.should_objectify("echo hello"));
        assert!(!config.should_objectify("ls -la"));
        assert!(!config.should_objectify("git status"));
    }

    #[test]
    fn docker_has_delim_hint() {
        let config = ObjectifyConfig::load();
        let hint = config.get_hint("docker ps -a").unwrap();
        assert_eq!(hint.delim.as_deref(), Some(r"\s{2,}"));
    }

    #[test]
    fn free_has_skip() {
        let config = ObjectifyConfig::load();
        let hint = config.get_hint("free -h").unwrap();
        assert_eq!(hint.skip, 1);
    }

    #[test]
    fn netstat_is_fixed() {
        let config = ObjectifyConfig::load();
        let hint = config.get_hint("netstat -an").unwrap();
        assert!(hint.fixed);
    }

    #[test]
    fn parse_yaml_config() {
        let yaml = r#"
mycommand:
  delim: '\s{3,}'
  skip: 2

simple:
"#;
        let parsed: HashMap<String, Option<ObjectifyHint>> =
            serde_yaml::from_str(yaml).unwrap();
        let my = parsed.get("mycommand").unwrap().as_ref().unwrap();
        assert_eq!(my.delim.as_deref(), Some(r"\s{3,}"));
        assert_eq!(my.skip, 2);
        // "simple:" with no value → None, which becomes default
        assert!(parsed.contains_key("simple"));
    }

    #[test]
    fn parse_yaml_with_cols() {
        let yaml = r#"
lsblk:
  cols: [NAME, SIZE, TYPE, MOUNTPOINT]
"#;
        let parsed: HashMap<String, Option<ObjectifyHint>> =
            serde_yaml::from_str(yaml).unwrap();
        let hint = parsed.get("lsblk").unwrap().as_ref().unwrap();
        assert_eq!(hint.cols.as_ref().unwrap(), &["NAME", "SIZE", "TYPE", "MOUNTPOINT"]);
    }

    #[test]
    fn load_from_yaml_file() {
        let tmp = std::env::temp_dir().join("rush-test-objectify");
        std::fs::create_dir_all(&tmp).unwrap();
        let config_path = tmp.join("objectify.yaml");
        std::fs::write(&config_path, r#"
mytool:
  skip: 2
  delim: '\t'

"docker stats":
  delim: '\s{3,}'
"#).unwrap();

        let mut commands = HashMap::new();
        load_yaml_file(&config_path.to_string_lossy(), &mut commands);

        assert!(commands.contains_key("mytool"), "should have mytool");
        let hint = &commands["mytool"];
        assert_eq!(hint.skip, 2);
        assert_eq!(hint.delim.as_deref(), Some("\\t"));

        assert!(commands.contains_key("docker stats"));
        assert_eq!(commands["docker stats"].delim.as_deref(), Some("\\s{3,}"));

        std::fs::remove_dir_all(&tmp).ok();
    }

    #[test]
    fn user_config_overrides_builtin() {
        // Verify that loading a user file overrides built-in defaults
        let tmp = std::env::temp_dir().join("rush-test-objectify-override");
        std::fs::create_dir_all(&tmp).unwrap();
        let config_path = tmp.join("objectify.yaml");
        std::fs::write(&config_path, "ps:\n  skip: 3\n").unwrap();

        let mut commands = built_in_defaults();
        // Before: ps has default (skip=0)
        assert_eq!(commands["ps"].skip, 0);

        load_yaml_file(&config_path.to_string_lossy(), &mut commands);
        // After: ps has skip=3 from user config
        assert_eq!(commands["ps"].skip, 3);

        std::fs::remove_dir_all(&tmp).ok();
    }

    #[test]
    fn empty_config_file() {
        let tmp = std::env::temp_dir().join("rush-test-objectify-empty");
        std::fs::create_dir_all(&tmp).unwrap();
        let config_path = tmp.join("objectify.yaml");
        std::fs::write(&config_path, "# just comments\n").unwrap();

        let mut commands = HashMap::new();
        load_yaml_file(&config_path.to_string_lossy(), &mut commands);
        // Should not crash, no commands added
        assert!(commands.is_empty());

        std::fs::remove_dir_all(&tmp).ok();
    }

    #[test]
    fn malformed_yaml_warns_but_continues() {
        let tmp = std::env::temp_dir().join("rush-test-objectify-malformed");
        std::fs::create_dir_all(&tmp).unwrap();
        let config_path = tmp.join("objectify.yaml");
        std::fs::write(&config_path, "this is not valid yaml: [[[").unwrap();

        let mut commands = HashMap::new();
        // Should not panic
        load_yaml_file(&config_path.to_string_lossy(), &mut commands);
        assert!(commands.is_empty());

        std::fs::remove_dir_all(&tmp).ok();
    }

    #[test]
    fn command_names_sorted() {
        let config = ObjectifyConfig::load();
        let names = config.command_names();
        assert!(!names.is_empty());
        // Verify sorted
        let mut sorted = names.clone();
        sorted.sort();
        assert_eq!(names, sorted);
    }

    #[test]
    fn hint_defaults() {
        let hint = ObjectifyHint::default();
        assert!(!hint.fixed);
        assert_eq!(hint.skip, 0);
        assert_eq!(hint.header, 0);
        assert!(hint.delim.is_none());
        assert!(hint.cols.is_none());
    }
}
