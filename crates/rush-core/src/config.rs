//! Rush configuration: ~/.config/rush/config.json

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RushConfig {
    #[serde(default = "default_vi")]
    pub edit_mode: String,
    #[serde(default = "default_history_size")]
    pub history_size: usize,
    #[serde(default = "default_true")]
    pub show_timing: bool,
    #[serde(default = "default_true")]
    pub show_tips: bool,
    #[serde(default)]
    pub stop_on_error: bool,
    #[serde(default)]
    pub trace_commands: bool,
    #[serde(default = "default_anthropic")]
    pub ai_provider: String,
    #[serde(default = "default_auto")]
    pub ai_model: String,
    #[serde(default)]
    pub bg: String,
    /// Chroma profile: "pastel", "muted" (default), "vibrant", or "mono".
    /// Applied to the whole palette when theming is active. See #228.
    #[serde(default = "default_flavor")]
    pub flavor: String,
    /// Optional accent hex (#RRGGBB). When set, overrides the Accent
    /// family hue so ssh-host / flag / secondary-emphasis roles pick
    /// up the user's color. Empty = use the default cyan accent. #228.
    #[serde(default)]
    pub accent: String,
    #[serde(default)]
    pub aliases: HashMap<String, String>,
}

fn default_vi() -> String { "vi".to_string() }
fn default_history_size() -> usize { 10_000 }
fn default_true() -> bool { true }
fn default_anthropic() -> String { "anthropic".to_string() }
fn default_auto() -> String { "auto".to_string() }
fn default_flavor() -> String { "muted".to_string() }

impl Default for RushConfig {
    fn default() -> Self {
        Self {
            edit_mode: "vi".into(),
            history_size: 10_000,
            show_timing: true,
            show_tips: true,
            stop_on_error: false,
            trace_commands: false,
            ai_provider: "anthropic".into(),
            ai_model: "auto".into(),
            bg: String::new(),
            flavor: "muted".into(),
            accent: String::new(),
            aliases: HashMap::new(),
        }
    }
}

impl RushConfig {
    /// Load config from ~/.config/rush/config.json. Returns default if missing.
    pub fn load() -> Self {
        let path = config_path();
        if !path.exists() {
            return Self::default();
        }
        match std::fs::read_to_string(&path) {
            Ok(content) => {
                // Strip // comments (JSONC support)
                let stripped = strip_jsonc_comments(&content);
                serde_json::from_str(&stripped).unwrap_or_else(|e| {
                    eprintln!("config.json: {e}");
                    Self::default()
                })
            }
            Err(e) => {
                eprintln!("config.json: {e}");
                Self::default()
            }
        }
    }

    /// Save config to ~/.config/rush/config.json.
    pub fn save(&self) -> Result<(), String> {
        let path = config_path();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let json = serde_json::to_string_pretty(self).map_err(|e| e.to_string())?;
        std::fs::write(&path, json).map_err(|e| e.to_string())
    }

    /// Get a setting by name.
    pub fn get(&self, key: &str) -> String {
        match key.to_lowercase().as_str() {
            "edit_mode" | "editmode" => self.edit_mode.clone(),
            "history_size" | "historysize" => self.history_size.to_string(),
            "show_timing" | "showtiming" => self.show_timing.to_string(),
            "show_tips" | "showtips" => self.show_tips.to_string(),
            "stop_on_error" | "stoponerror" => self.stop_on_error.to_string(),
            "trace_commands" | "tracecommands" => self.trace_commands.to_string(),
            "ai_provider" | "aiprovider" => self.ai_provider.clone(),
            "ai_model" | "aimodel" => self.ai_model.clone(),
            "bg" => self.bg.clone(),
            _ => String::new(),
        }
    }

    /// Set a setting by name. Returns true if valid.
    pub fn set(&mut self, key: &str, value: &str) -> bool {
        match key.to_lowercase().as_str() {
            "edit_mode" | "editmode" | "vi" | "emacs" => {
                let mode = if key == "vi" || key == "emacs" { key } else { value };
                if mode != "vi" && mode != "emacs" {
                    return false;
                }
                self.edit_mode = mode.to_string();
                true
            }
            "history_size" | "historysize" => {
                if let Ok(n) = value.parse() {
                    self.history_size = n;
                    true
                } else {
                    false
                }
            }
            "show_timing" | "showtiming" => {
                self.show_timing = value == "true" || value == "on" || value == "1";
                true
            }
            "show_tips" | "showtips" => {
                self.show_tips = value == "true" || value == "on" || value == "1"
                    || value.is_empty(); // bare "set show_tips" toggles on
                true
            }
            "stop_on_error" | "stoponerror" => {
                self.stop_on_error = value == "true" || value == "on" || value == "1";
                true
            }
            "trace_commands" | "tracecommands" => {
                self.trace_commands = value == "true" || value == "on" || value == "1";
                true
            }
            "ai_provider" | "aiprovider" => {
                self.ai_provider = value.to_string();
                true
            }
            "ai_model" | "aimodel" => {
                self.ai_model = value.to_string();
                true
            }
            "bg" => {
                self.bg = value.to_string();
                true
            }
            "flavor" => {
                self.flavor = value.to_string();
                true
            }
            "accent" => {
                self.accent = value.to_string();
                true
            }
            _ => false,
        }
    }

    /// Check if startup tips are enabled.
    pub fn show_tips(&self) -> bool {
        self.show_tips
    }

    /// Display all settings.
    pub fn display(&self) {
        println!("edit_mode:      {}", self.edit_mode);
        println!("history_size:   {}", self.history_size);
        println!("show_timing:    {}", self.show_timing);
        println!("show_tips:      {}", self.show_tips);
        println!("stop_on_error:  {}", self.stop_on_error);
        println!("trace_commands: {}", self.trace_commands);
        println!("ai_provider:    {}", self.ai_provider);
        println!("ai_model:       {}", self.ai_model);
        if !self.aliases.is_empty() {
            println!("aliases:        {} defined", self.aliases.len());
        }
    }
}

fn config_path() -> PathBuf {
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_else(|_| ".".to_string());
    PathBuf::from(home).join(".config").join("rush").join("config.json")
}

/// Strip // line comments from JSONC content.
fn strip_jsonc_comments(input: &str) -> String {
    let mut result = String::with_capacity(input.len());
    let mut in_string = false;
    let chars: Vec<char> = input.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if in_string {
            if chars[i] == '\\' && i + 1 < chars.len() {
                result.push(chars[i]);
                result.push(chars[i + 1]);
                i += 2;
                continue;
            }
            if chars[i] == '"' {
                in_string = false;
            }
            result.push(chars[i]);
        } else {
            if chars[i] == '"' {
                in_string = true;
                result.push(chars[i]);
            } else if chars[i] == '/' && i + 1 < chars.len() && chars[i + 1] == '/' {
                // Skip rest of line
                while i < chars.len() && chars[i] != '\n' {
                    i += 1;
                }
                continue;
            } else {
                result.push(chars[i]);
            }
        }
        i += 1;
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_config() {
        let config = RushConfig::default();
        assert_eq!(config.edit_mode, "vi");
        assert_eq!(config.history_size, 10_000);
        assert!(config.show_timing);
        assert_eq!(config.ai_provider, "anthropic");
    }

    #[test]
    fn config_get_set() {
        let mut config = RushConfig::default();
        assert!(config.set("edit_mode", "emacs"));
        assert_eq!(config.get("edit_mode"), "emacs");
        assert!(config.set("ai_provider", "openai"));
        assert_eq!(config.get("ai_provider"), "openai");
        assert!(!config.set("nonexistent", "value"));
    }

    #[test]
    fn config_serialize_deserialize() {
        let config = RushConfig::default();
        let json = serde_json::to_string(&config).unwrap();
        let parsed: RushConfig = serde_json::from_str(&json).unwrap();
        assert_eq!(parsed.edit_mode, "vi");
        assert_eq!(parsed.history_size, 10_000);
    }

    #[test]
    fn strip_jsonc() {
        let input = r#"{ "a": 1, // comment
  "b": "hello // not a comment" }"#;
        let stripped = strip_jsonc_comments(input);
        let parsed: serde_json::Value = serde_json::from_str(&stripped).unwrap();
        assert_eq!(parsed["a"], 1);
        assert_eq!(parsed["b"], "hello // not a comment");
    }

    #[test]
    fn set_shorthand() {
        let mut config = RushConfig::default();
        assert!(config.set("vi", ""));
        assert_eq!(config.edit_mode, "vi");
        assert!(config.set("emacs", ""));
        assert_eq!(config.edit_mode, "emacs");
    }
}
