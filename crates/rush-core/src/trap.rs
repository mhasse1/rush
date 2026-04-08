//! POSIX trap command implementation.
//! trap [action] [signal...]
//! action: command string, '' (ignore), - (reset to default)
//! signal: EXIT (0), signal names or numbers

use std::collections::HashMap;
use std::sync::Mutex;

/// Global trap registry.
static TRAPS: Mutex<Option<HashMap<String, String>>> = Mutex::new(None);

/// Initialize the trap registry.
pub fn init() {
    let mut traps = TRAPS.lock().unwrap();
    if traps.is_none() {
        *traps = Some(HashMap::new());
    }
}

/// Set a trap.
pub fn set_trap(signal: &str, action: &str) {
    init();
    let mut traps = TRAPS.lock().unwrap();
    let map = traps.as_mut().unwrap();

    let signal = normalize_signal(signal);

    if action == "-" {
        // Reset to default
        map.remove(&signal);
        // Reset actual signal disposition
        #[cfg(unix)]
        if let Some(signum) = signal_number(&signal) {
            unsafe { libc::signal(signum, libc::SIG_DFL); }
        }
    } else if action.is_empty() {
        // Ignore signal
        map.insert(signal.clone(), String::new());
        #[cfg(unix)]
        if let Some(signum) = signal_number(&signal) {
            unsafe { libc::signal(signum, libc::SIG_IGN); }
        }
    } else {
        // Set command handler
        map.insert(signal, action.to_string());
        // Note: actual signal-triggered execution requires async signal
        // handling which is complex. For now, EXIT trap is checked on exit.
    }
}

/// Get the trap action for a signal. Returns None if no trap set.
pub fn get_trap(signal: &str) -> Option<String> {
    let traps = TRAPS.lock().unwrap();
    traps.as_ref()?.get(&normalize_signal(signal)).cloned()
}

/// Get the EXIT trap action.
pub fn get_exit_trap() -> Option<String> {
    get_trap("EXIT")
}

/// List all traps.
pub fn list_traps() {
    let traps = TRAPS.lock().unwrap();
    if let Some(map) = traps.as_ref() {
        for (signal, action) in map {
            if action.is_empty() {
                println!("trap -- '' {signal}");
            } else {
                println!("trap -- '{action}' {signal}");
            }
        }
    }
}

/// Parse and execute a trap command.
/// Returns true if handled as a trap command.
pub fn handle_trap(args: &str) -> bool {
    let args = args.trim();

    if args.is_empty() || args == "-l" {
        list_traps();
        return true;
    }

    // Parse: trap 'action' signal [signal...]
    // or: trap - signal [signal...]
    // or: trap '' signal [signal...]
    let parts = shell_split(args);
    if parts.len() < 2 {
        // trap action — missing signal
        if parts.len() == 1 && parts[0] == "-p" {
            list_traps();
            return true;
        }
        eprintln!("trap: usage: trap [action] signal ...");
        return true;
    }

    let action = &parts[0];
    for signal in &parts[1..] {
        set_trap(signal, action);
    }
    true
}

fn normalize_signal(sig: &str) -> String {
    match sig.to_uppercase().as_str() {
        "0" | "EXIT" => "EXIT".to_string(),
        "1" | "HUP" | "SIGHUP" => "HUP".to_string(),
        "2" | "INT" | "SIGINT" => "INT".to_string(),
        "3" | "QUIT" | "SIGQUIT" => "QUIT".to_string(),
        "9" | "KILL" | "SIGKILL" => "KILL".to_string(),
        "15" | "TERM" | "SIGTERM" => "TERM".to_string(),
        "20" | "TSTP" | "SIGTSTP" => "TSTP".to_string(),
        other => other.to_uppercase(),
    }
}

#[cfg(unix)]
fn signal_number(name: &str) -> Option<libc::c_int> {
    match name {
        "HUP" => Some(libc::SIGHUP),
        "INT" => Some(libc::SIGINT),
        "QUIT" => Some(libc::SIGQUIT),
        "KILL" => Some(libc::SIGKILL),
        "TERM" => Some(libc::SIGTERM),
        "TSTP" => Some(libc::SIGTSTP),
        "PIPE" => Some(libc::SIGPIPE),
        _ => None,
    }
}

/// Simple shell-style word splitting (respects quotes).
fn shell_split(s: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut current = String::new();
    let mut in_single = false;
    let mut in_double = false;

    for ch in s.chars() {
        match ch {
            '\'' if !in_double => { in_single = !in_single; continue; }
            '"' if !in_single => { in_double = !in_double; continue; }
            ' ' | '\t' if !in_single && !in_double => {
                if !current.is_empty() {
                    parts.push(std::mem::take(&mut current));
                }
                continue;
            }
            _ => current.push(ch),
        }
    }
    if !current.is_empty() {
        parts.push(current);
    }
    parts
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn set_and_get_exit_trap() {
        init();
        set_trap("EXIT", "echo bye");
        assert_eq!(get_exit_trap(), Some("echo bye".to_string()));
        set_trap("EXIT", "-");
        assert_eq!(get_exit_trap(), None);
    }

    #[test]
    fn normalize_signals() {
        assert_eq!(normalize_signal("0"), "EXIT");
        assert_eq!(normalize_signal("EXIT"), "EXIT");
        assert_eq!(normalize_signal("2"), "INT");
        assert_eq!(normalize_signal("SIGINT"), "INT");
        assert_eq!(normalize_signal("HUP"), "HUP");
    }

    #[test]
    fn ignore_trap() {
        init();
        set_trap("INT", "");
        assert_eq!(get_trap("INT"), Some(String::new()));
        set_trap("INT", "-");
    }
}
