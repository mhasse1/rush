//! Rush stdlib: File, Dir, Time — native implementations (no PowerShell).

use std::cell::Cell;
use std::collections::HashMap;
use std::fs;
use std::path::Path;
use std::time::SystemTime;

use crate::value::Value;

thread_local! {
    /// Set by stdlib methods when a recoverable error occurs.
    /// The evaluator clears this before a stdlib call and checks it after,
    /// so script-level exit codes reflect stdlib failures.
    static LAST_ERROR: Cell<bool> = const { Cell::new(false) };
}

/// Record a stdlib error — prints to stderr and flags for the evaluator.
pub(crate) fn stdlib_err(msg: impl std::fmt::Display) {
    eprintln!("{msg}");
    LAST_ERROR.with(|e| e.set(true));
}

/// True if any stdlib method has signaled an error since the last reset.
pub fn take_last_error() -> bool {
    LAST_ERROR.with(|e| e.replace(false))
}

/// Reset the stdlib error flag without consuming it.
pub fn reset_last_error() {
    LAST_ERROR.with(|e| e.set(false));
}

// ── File ────────────────────────────────────────────────────────────

pub fn file_method(method: &str, args: &[Value]) -> Value {
    match method {
        "read" => {
            let path = arg_str(args, 0);
            match fs::read_to_string(&path) {
                Ok(content) => Value::String(content),
                Err(e) => {
                    stdlib_err(format!("File.read: {path}: {e}"));
                    Value::Nil
                }
            }
        }
        "read_lines" => {
            let path = arg_str(args, 0);
            match fs::read_to_string(&path) {
                Ok(content) => {
                    Value::Array(content.lines().map(|l| Value::String(l.to_string())).collect())
                }
                Err(e) => {
                    stdlib_err(format!("File.read_lines: {path}: {e}"));
                    Value::Array(Vec::new())
                }
            }
        }
        "read_json" => {
            let path = arg_str(args, 0);
            match fs::read_to_string(&path) {
                Ok(content) => parse_json_to_value(&content),
                Err(e) => {
                    stdlib_err(format!("File.read_json: {path}: {e}"));
                    Value::Nil
                }
            }
        }
        "write" => {
            let path = arg_str(args, 0);
            let content = args.get(1).map(|v| v.to_rush_string()).unwrap_or_default();
            match fs::write(&path, &content) {
                Ok(()) => Value::Bool(true),
                Err(e) => {
                    stdlib_err(format!("File.write: {path}: {e}"));
                    Value::Bool(false)
                }
            }
        }
        "append" => {
            let path = arg_str(args, 0);
            let content = args.get(1).map(|v| v.to_rush_string()).unwrap_or_default();
            match std::fs::OpenOptions::new()
                .append(true)
                .create(true)
                .open(&path)
            {
                Ok(mut file) => {
                    use std::io::Write;
                    match file.write_all(content.as_bytes()) {
                        Ok(()) => Value::Bool(true),
                        Err(e) => {
                            stdlib_err(format!("File.append: {path}: {e}"));
                            Value::Bool(false)
                        }
                    }
                }
                Err(e) => {
                    stdlib_err(format!("File.append: {path}: {e}"));
                    Value::Bool(false)
                }
            }
        }
        "exist?" | "exists" | "exists?" => {
            let path = arg_str(args, 0);
            Value::Bool(Path::new(&path).is_file())
        }
        "delete" | "remove" => {
            let path = arg_str(args, 0);
            match fs::remove_file(&path) {
                Ok(()) => Value::Bool(true),
                Err(e) => {
                    stdlib_err(format!("File.delete: {path}: {e}"));
                    Value::Bool(false)
                }
            }
        }
        "copy" => {
            let src = arg_str(args, 0);
            let dst = arg_str(args, 1);
            match fs::copy(&src, &dst) {
                Ok(_) => Value::Bool(true),
                Err(e) => {
                    stdlib_err(format!("File.copy: {src} → {dst}: {e}"));
                    Value::Bool(false)
                }
            }
        }
        "move" | "rename" => {
            let src = arg_str(args, 0);
            let dst = arg_str(args, 1);
            match fs::rename(&src, &dst) {
                Ok(()) => Value::Bool(true),
                Err(e) => {
                    stdlib_err(format!("File.move: {src} → {dst}: {e}"));
                    Value::Bool(false)
                }
            }
        }
        "size" => {
            let path = arg_str(args, 0);
            match fs::metadata(&path) {
                Ok(meta) => Value::Int(meta.len() as i64),
                Err(_) => Value::Int(-1),
            }
        }
        "ext" | "extension" => {
            let path = arg_str(args, 0);
            Value::String(
                Path::new(&path)
                    .extension()
                    .map(|e| e.to_string_lossy().to_string())
                    .unwrap_or_default(),
            )
        }
        "basename" | "name" => {
            let path = arg_str(args, 0);
            Value::String(
                Path::new(&path)
                    .file_name()
                    .map(|n| n.to_string_lossy().to_string())
                    .unwrap_or_default(),
            )
        }
        "dirname" | "directory" => {
            let path = arg_str(args, 0);
            Value::String(
                Path::new(&path)
                    .parent()
                    .map(|p| p.to_string_lossy().to_string())
                    .unwrap_or_default(),
            )
        }
        "separator" => Value::String(std::path::MAIN_SEPARATOR.to_string()),
        _ => Value::Nil,
    }
}

// ── Dir ─────────────────────────────────────────────────────────────

pub fn dir_method(method: &str, args: &[Value]) -> Value {
    match method {
        "list" => {
            let path = if args.is_empty() {
                ".".to_string()
            } else {
                arg_str(args, 0)
            };

            // Check for symbol flags
            let files_only = args.iter().any(|a| matches!(a, Value::Symbol(s) if s == ":files"));
            let dirs_only = args.iter().any(|a| matches!(a, Value::Symbol(s) if s == ":dirs"));
            let recurse = args.iter().any(|a| matches!(a, Value::Symbol(s) if s == ":recurse"));
            let hidden = args.iter().any(|a| matches!(a, Value::Symbol(s) if s == ":hidden"));

            let mut entries = Vec::new();
            collect_dir_entries(&path, &mut entries, recurse, files_only, dirs_only, hidden);
            Value::Array(entries)
        }
        "exist?" | "exists" | "exists?" => {
            let path = arg_str(args, 0);
            Value::Bool(Path::new(&path).is_dir())
        }
        "mkdir" => {
            let path = arg_str(args, 0);
            match fs::create_dir_all(&path) {
                Ok(()) => Value::Bool(true),
                Err(e) => {
                    stdlib_err(format!("Dir.mkdir: {path}: {e}"));
                    Value::Bool(false)
                }
            }
        }
        "rmdir" | "delete" => {
            let path = arg_str(args, 0);
            match fs::remove_dir_all(&path) {
                Ok(()) => Value::Bool(true),
                Err(e) => {
                    stdlib_err(format!("Dir.rmdir: {path}: {e}"));
                    Value::Bool(false)
                }
            }
        }
        "pwd" | "current" => {
            Value::String(std::env::current_dir().map(|p| p.to_string_lossy().to_string()).unwrap_or_default())
        }
        "home" => {
            Value::String(std::env::var("HOME").unwrap_or_else(|_| {
                std::env::var("USERPROFILE").unwrap_or_default()
            }))
        }
        "glob" => {
            let pattern = arg_str(args, 0);
            match glob_pattern(&pattern) {
                Ok(files) => Value::Array(files.into_iter().map(Value::String).collect()),
                Err(e) => {
                    stdlib_err(format!("Dir.glob: {e}"));
                    Value::Array(Vec::new())
                }
            }
        }
        _ => Value::Nil,
    }
}

fn collect_dir_entries(
    path: &str,
    entries: &mut Vec<Value>,
    recurse: bool,
    files_only: bool,
    dirs_only: bool,
    hidden: bool,
) {
    let read_dir = match fs::read_dir(path) {
        Ok(rd) => rd,
        Err(_) => return,
    };
    for entry in read_dir.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if !hidden && name.starts_with('.') {
            continue;
        }
        let is_dir = entry.file_type().map(|t| t.is_dir()).unwrap_or(false);
        if files_only && is_dir {
            if recurse {
                collect_dir_entries(
                    &entry.path().to_string_lossy(),
                    entries,
                    true,
                    files_only,
                    dirs_only,
                    hidden,
                );
            }
            continue;
        }
        if dirs_only && !is_dir {
            continue;
        }
        // Non-recursive mode returns basenames (the caller knows the dir);
        // recursive mode returns full joined paths so each entry carries
        // its parent chain and scripts can reconstruct the file's location
        // without a parallel bookkeeping pass (#257).
        let display = if recurse {
            entry.path().to_string_lossy().to_string()
        } else {
            name.clone()
        };
        entries.push(Value::String(display));
        if recurse && is_dir {
            collect_dir_entries(
                &entry.path().to_string_lossy(),
                entries,
                true,
                files_only,
                dirs_only,
                hidden,
            );
        }
    }
}

/// Simple glob implementation (supports * and ** patterns).
fn glob_pattern(pattern: &str) -> Result<Vec<String>, String> {
    // Use walkdir-style approach for simple patterns
    let path = Path::new(pattern);
    let dir = path.parent().unwrap_or(Path::new("."));
    let file_pattern = path
        .file_name()
        .map(|f| f.to_string_lossy().to_string())
        .unwrap_or_else(|| "*".to_string());

    let mut results = Vec::new();
    if let Ok(entries) = fs::read_dir(dir) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if matches_glob(&name, &file_pattern) {
                results.push(entry.path().to_string_lossy().to_string());
            }
        }
    }
    results.sort();
    Ok(results)
}

fn matches_glob(name: &str, pattern: &str) -> bool {
    if pattern == "*" {
        return true;
    }
    if let Some(ext) = pattern.strip_prefix("*.") {
        return name.ends_with(&format!(".{ext}"));
    }
    if let Some(prefix) = pattern.strip_suffix('*') {
        return name.starts_with(prefix);
    }
    name == pattern
}

// ── Time ────────────────────────────────────────────────────────────

pub fn time_method(method: &str, _args: &[Value]) -> Value {
    match method {
        "now" => {
            let now = chrono_now();
            Value::String(now)
        }
        "utc_now" => {
            let now = chrono_utc_now();
            Value::String(now)
        }
        "today" => {
            let today = chrono_today();
            Value::String(today)
        }
        "epoch" | "unix" => {
            let epoch = SystemTime::now()
                .duration_since(SystemTime::UNIX_EPOCH)
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0);
            Value::Int(epoch)
        }
        _ => Value::Nil,
    }
}

fn chrono_now() -> String {
    // Use system time formatting without chrono dependency
    let now = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    format_unix_timestamp(now.as_secs() as i64, true)
}

fn chrono_utc_now() -> String {
    let now = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    format_unix_timestamp_utc(now.as_secs() as i64)
}

fn chrono_today() -> String {
    let now = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = now.as_secs() as i64;
    let days = secs / 86400;
    // Simple date calculation from epoch
    let (y, m, d) = days_to_date(days);
    format!("{y:04}-{m:02}-{d:02}")
}

fn format_unix_timestamp(epoch_secs: i64, _local: bool) -> String {
    // Get local time via libc on Unix, or fallback to UTC
    #[cfg(unix)]
    {
        
        unsafe {
            let t = epoch_secs as libc::time_t;
            let mut tm: libc::tm = std::mem::zeroed();
            libc::localtime_r(&t, &mut tm);
            let mut buf = [0u8; 64];
            let fmt = c"%Y-%m-%d %H:%M:%S";
            let len = libc::strftime(
                buf.as_mut_ptr() as *mut libc::c_char,
                buf.len(),
                fmt.as_ptr(),
                &tm,
            );
            String::from_utf8_lossy(&buf[..len]).to_string()
        }
    }
    #[cfg(not(unix))]
    {
        format_unix_timestamp_utc(epoch_secs)
    }
}

fn format_unix_timestamp_utc(epoch_secs: i64) -> String {
    let (y, m, d) = days_to_date(epoch_secs / 86400);
    let rem = epoch_secs % 86400;
    let h = rem / 3600;
    let min = (rem % 3600) / 60;
    let s = rem % 60;
    format!("{y:04}-{m:02}-{d:02} {h:02}:{min:02}:{s:02} UTC")
}

/// Convert days since epoch to (year, month, day).
fn days_to_date(days_since_epoch: i64) -> (i64, i64, i64) {
    // Algorithm from http://howardhinnant.github.io/date_algorithms.html
    let z = days_since_epoch + 719468;
    let era = if z >= 0 { z } else { z - 146096 } / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    (y, m, d)
}

// ── Duration ────────────────────────────────────────────────────────

/// Convert a duration property access (e.g., 2.hours) to seconds.
pub fn duration_to_seconds(n: f64, unit: &str) -> Value {
    let secs = match unit {
        "seconds" | "second" => n,
        "minutes" | "minute" => n * 60.0,
        "hours" | "hour" => n * 3600.0,
        "days" | "day" => n * 86400.0,
        _ => return Value::Nil,
    };
    if secs == secs.floor() {
        Value::Int(secs as i64)
    } else {
        Value::Float(secs)
    }
}

// ── Env ─────────────────────────────────────────────────────────────

pub fn env_get(key: &str) -> Value {
    match std::env::var(key) {
        Ok(val) => Value::String(val),
        Err(_) => Value::Nil,
    }
}

pub fn env_set(key: &str, value: &str) {
    // SAFETY: Rush is single-threaded; no concurrent env access.
    unsafe { std::env::set_var(key, value) };
}

pub fn env_all() -> Value {
    let mut map = HashMap::new();
    for (key, val) in std::env::vars() {
        map.insert(key, Value::String(val));
    }
    Value::Hash(map)
}

// ── JSON parsing ────────────────────────────────────────────────────

fn parse_json_to_value(json: &str) -> Value {
    match serde_json::from_str::<serde_json::Value>(json) {
        Ok(v) => json_to_value(&v),
        Err(_) => Value::Nil,
    }
}

fn json_to_value(v: &serde_json::Value) -> Value {
    match v {
        serde_json::Value::Null => Value::Nil,
        serde_json::Value::Bool(b) => Value::Bool(*b),
        serde_json::Value::Number(n) => {
            if let Some(i) = n.as_i64() {
                Value::Int(i)
            } else if let Some(f) = n.as_f64() {
                Value::Float(f)
            } else {
                Value::Nil
            }
        }
        serde_json::Value::String(s) => Value::String(s.clone()),
        serde_json::Value::Array(arr) => {
            Value::Array(arr.iter().map(json_to_value).collect())
        }
        serde_json::Value::Object(obj) => {
            let mut map = HashMap::new();
            for (k, v) in obj {
                map.insert(k.clone(), json_to_value(v));
            }
            Value::Hash(map)
        }
    }
}

// ── Path ────────────────────────────────────────────────────────────

pub fn path_method(method: &str, args: &[Value]) -> Value {
    match method {
        // Path.sep — platform-native separator ("/" or "\")
        "sep" => Value::String(std::path::MAIN_SEPARATOR.to_string()),

        // Path.join("a", "b", "c") — join with forward slashes (Rush-style)
        "join" => {
            let parts: Vec<String> = args.iter().map(|v| v.to_rush_string()).collect();
            Value::String(parts.join("/"))
        }

        // Path.normalize(path) — canonical Rush path (forward slashes)
        "normalize" => {
            let p = arg_str(args, 0);
            Value::String(p.replace('\\', "/"))
        }

        // Path.native(path) — convert to platform-native separators
        "native" => {
            let p = arg_str(args, 0);
            if cfg!(windows) {
                Value::String(p.replace('/', "\\"))
            } else {
                Value::String(p)
            }
        }

        // Path.expand(path) — expand ~ and env vars, normalize
        "expand" => {
            let p = arg_str(args, 0);
            let expanded = if p.starts_with("~/") || p == "~" {
                let home = std::env::var("HOME")
                    .or_else(|_| std::env::var("USERPROFILE"))
                    .unwrap_or_default()
                    .replace('\\', "/");
                if p == "~" { home } else { format!("{home}{}", &p[1..]) }
            } else {
                p
            };
            Value::String(expanded.replace('\\', "/"))
        }

        // Path.exist?(path) — check if path exists
        "exist?" | "exists?" => {
            let p = arg_str(args, 0);
            Value::Bool(Path::new(&p).exists())
        }

        // Path.absolute?(path) — check if path is absolute
        "absolute?" => {
            let p = arg_str(args, 0);
            Value::Bool(Path::new(&p).is_absolute())
        }

        // Path.ext(path) — file extension
        "ext" | "extension" => {
            let p = arg_str(args, 0);
            Value::String(
                Path::new(&p).extension()
                    .map(|e| format!(".{}", e.to_string_lossy()))
                    .unwrap_or_default()
            )
        }

        // Path.basename(path) — file name
        "basename" => {
            let p = arg_str(args, 0);
            Value::String(
                Path::new(&p).file_name()
                    .map(|n| n.to_string_lossy().to_string())
                    .unwrap_or_default()
            )
        }

        // Path.dirname(path) — parent directory
        "dirname" => {
            let p = arg_str(args, 0);
            Value::String(
                Path::new(&p).parent()
                    .map(|d| d.to_string_lossy().replace('\\', "/"))
                    .unwrap_or_default()
            )
        }

        _ => Value::Nil,
    }
}

// ── Helpers ─────────────────────────────────────────────────────────

fn arg_str(args: &[Value], index: usize) -> String {
    args.get(index)
        .map(|v| v.to_rush_string())
        .unwrap_or_default()
}

// ── Ssh ────────────────────────────────────────────────────────────

pub fn ssh_method(method: &str, args: &[Value]) -> Value {
    match method {
        // Ssh.run(host_or_chain, command) — execute command on remote host.
        // host_or_chain accepts:
        //   "target"                    — single host (legacy)
        //   "bastion,target"            — ProxyJump chain (comma-separated)
        //   ["bastion", "target"]       — explicit hop list
        //   ["bastion", "mid", "target"] — multi-hop chain
        "run" => {
            let chain = match parse_chain(args.first()) {
                Ok(c) => c,
                Err(e) => {
                    stdlib_err(format!("Ssh.run: {e}"));
                    return Value::Nil;
                }
            };
            let command = arg_str(args, 1);
            if command.is_empty() {
                stdlib_err("Ssh.run: requires host(s) and command".to_string());
                return Value::Nil;
            }
            ssh_execute(&chain, &command)
        }

        // Ssh.test(host_or_chain) — test connectivity (returns bool).
        // Accepts the same chain forms as Ssh.run.
        "test" => {
            let chain = match parse_chain(args.first()) {
                Ok(c) => c,
                Err(_) => return Value::Bool(false),
            };
            let mut cmd = std::process::Command::new("ssh");
            for arg in build_ssh_args(&chain, &["-o", "BatchMode=yes", "-o", "ConnectTimeout=5"]) {
                cmd.arg(arg);
            }
            cmd.arg("echo ok");
            Value::Bool(cmd.output().is_ok_and(|o| o.status.success()))
        }

        _ => {
            stdlib_err(format!("Ssh.{method}: unknown method"));
            Value::Nil
        }
    }
}

/// Parse a host argument into a normalized hop chain.
/// Accepts a single host string, a comma-separated chain string,
/// or an array of host strings. Empty hops are stripped; leading
/// and trailing whitespace trimmed.
pub(crate) fn parse_chain(arg: Option<&Value>) -> Result<Vec<String>, String> {
    let value = arg.ok_or_else(|| "missing host".to_string())?;
    let hops: Vec<String> = match value {
        Value::String(s) => s
            .split(',')
            .map(|h| h.trim().to_string())
            .filter(|h| !h.is_empty())
            .collect(),
        Value::Array(arr) => arr
            .iter()
            .filter_map(|v| match v {
                Value::String(s) => {
                    let t = s.trim();
                    if t.is_empty() { None } else { Some(t.to_string()) }
                }
                _ => None,
            })
            .collect(),
        Value::Nil => return Err("missing host".into()),
        _ => return Err("host must be a string or array of strings".into()),
    };
    if hops.is_empty() {
        return Err("empty host chain".into());
    }
    Ok(hops)
}

/// Build the ssh argv prefix for the given hop chain.
/// For single-host chains, just emits the standard options + host.
/// For multi-hop chains, inserts `-J <bastion[,intermediate]...>`
/// before the final destination.
pub(crate) fn build_ssh_args(hops: &[String], options: &[&str]) -> Vec<String> {
    let mut args: Vec<String> = options.iter().map(|s| s.to_string()).collect();
    if hops.len() > 1 {
        args.push("-J".to_string());
        args.push(hops[..hops.len() - 1].join(","));
    }
    args.push(hops[hops.len() - 1].clone());
    args
}

fn ssh_execute(chain: &[String], command: &str) -> Value {
    let final_host = chain[chain.len() - 1].clone();
    let chain_str = chain.join(",");
    let chain_value = Value::Array(chain.iter().map(|h| Value::String(h.clone())).collect());

    let args = build_ssh_args(chain, &["-o", "BatchMode=yes", "-o", "ConnectTimeout=10"]);
    let mut cmd = std::process::Command::new("ssh");
    for a in &args {
        cmd.arg(a);
    }
    cmd.arg(command);

    match cmd.output() {
        Ok(output) => {
            let stdout = String::from_utf8_lossy(&output.stdout).trim_end().to_string();
            let stderr = String::from_utf8_lossy(&output.stderr).trim_end().to_string();
            let code = output.status.code().unwrap_or(-1);

            let mut hash = HashMap::new();
            hash.insert("status".to_string(),
                Value::String(if code == 0 { "success" } else { "error" }.to_string()));
            hash.insert("exit_code".to_string(), Value::Int(code as i64));
            hash.insert("stdout".to_string(), Value::String(stdout));
            hash.insert("stderr".to_string(), Value::String(stderr));
            hash.insert("host".to_string(), Value::String(final_host));
            hash.insert("chain".to_string(), chain_value);
            hash.insert("chain_str".to_string(), Value::String(chain_str));
            Value::Hash(hash)
        }
        Err(e) => {
            let mut hash = HashMap::new();
            hash.insert("status".to_string(), Value::String("error".to_string()));
            hash.insert("exit_code".to_string(), Value::Int(1));
            hash.insert("stdout".to_string(), Value::String(String::new()));
            hash.insert("stderr".to_string(), Value::String(format!("SSH error: {e}")));
            hash.insert("host".to_string(), Value::String(final_host));
            hash.insert("chain".to_string(), chain_value);
            hash.insert("chain_str".to_string(), Value::String(chain_str));
            Value::Hash(hash)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── File ────────────────────────────────────────────────────────

    #[test]
    fn file_write_and_read() {
        let tmp = std::env::temp_dir().join("rush_test_file.txt");
        let path = tmp.to_string_lossy().to_string();

        file_method("write", &[Value::String(path.clone()), Value::String("hello rush".to_string())]);
        let content = file_method("read", &[Value::String(path.clone())]);
        assert_eq!(content, Value::String("hello rush".to_string()));

        file_method("delete", &[Value::String(path)]);
    }

    #[test]
    fn file_exist() {
        let tmp = std::env::temp_dir().join("rush_test_exist.txt");
        let path = tmp.to_string_lossy().to_string();
        // File doesn't exist yet
        assert_eq!(file_method("exist?", &[Value::String(path.clone())]), Value::Bool(false));
        // Create it
        file_method("write", &[Value::String(path.clone()), Value::String("x".to_string())]);
        assert_eq!(file_method("exist?", &[Value::String(path.clone())]), Value::Bool(true));
        file_method("delete", &[Value::String(path)]);
    }

    #[test]
    fn file_size() {
        let tmp = std::env::temp_dir().join("rush_test_size.txt");
        let path = tmp.to_string_lossy().to_string();
        file_method("write", &[Value::String(path.clone()), Value::String("12345".to_string())]);
        let size = file_method("size", &[Value::String(path.clone())]);
        assert_eq!(size, Value::Int(5));
        file_method("delete", &[Value::String(path)]);
    }

    #[test]
    fn file_basename_dirname_ext() {
        let p = Value::String("/usr/local/bin/rush.exe".to_string());
        assert_eq!(file_method("basename", std::slice::from_ref(&p)), Value::String("rush.exe".to_string()));
        assert_eq!(file_method("dirname", std::slice::from_ref(&p)), Value::String("/usr/local/bin".to_string()));
        assert_eq!(file_method("ext", &[p]), Value::String("exe".to_string()));
    }

    #[test]
    fn file_append() {
        let tmp = std::env::temp_dir().join("rush_test_append.txt");
        let path = tmp.to_string_lossy().to_string();
        file_method("write", &[Value::String(path.clone()), Value::String("line1\n".to_string())]);
        file_method("append", &[Value::String(path.clone()), Value::String("line2\n".to_string())]);
        let content = file_method("read", &[Value::String(path.clone())]);
        assert_eq!(content, Value::String("line1\nline2\n".to_string()));
        file_method("delete", &[Value::String(path)]);
    }

    #[test]
    fn file_copy() {
        let tmp1 = std::env::temp_dir().join("rush_test_copy_src.txt");
        let tmp2 = std::env::temp_dir().join("rush_test_copy_dst.txt");
        let p1 = tmp1.to_string_lossy().to_string();
        let p2 = tmp2.to_string_lossy().to_string();
        file_method("write", &[Value::String(p1.clone()), Value::String("data".to_string())]);
        file_method("copy", &[Value::String(p1.clone()), Value::String(p2.clone())]);
        let content = file_method("read", &[Value::String(p2.clone())]);
        assert_eq!(content, Value::String("data".to_string()));
        file_method("delete", &[Value::String(p1)]);
        file_method("delete", &[Value::String(p2)]);
    }

    // ── Dir ─────────────────────────────────────────────────────────

    #[test]
    fn dir_pwd() {
        let pwd = dir_method("pwd", &[]);
        assert!(matches!(pwd, Value::String(s) if !s.is_empty()));
    }

    #[test]
    fn dir_home() {
        let home = dir_method("home", &[]);
        assert!(matches!(home, Value::String(s) if !s.is_empty()));
    }

    #[test]
    fn dir_mkdir_exist_rmdir() {
        let tmp = std::env::temp_dir().join("rush_test_dir");
        let path = tmp.to_string_lossy().to_string();

        dir_method("mkdir", &[Value::String(path.clone())]);
        assert_eq!(dir_method("exist?", &[Value::String(path.clone())]), Value::Bool(true));

        dir_method("rmdir", &[Value::String(path.clone())]);
        assert_eq!(dir_method("exist?", &[Value::String(path)]), Value::Bool(false));
    }

    #[test]
    fn dir_list() {
        let tmp = std::env::temp_dir().to_string_lossy().replace('\\', "/");
        let list = dir_method("list", &[Value::String(tmp)]);
        assert!(matches!(list, Value::Array(arr) if !arr.is_empty()));
    }

    // ── Time ────────────────────────────────────────────────────────

    #[test]
    fn time_now() {
        let now = time_method("now", &[]);
        assert!(matches!(now, Value::String(s) if s.contains('-')));
    }

    #[test]
    fn time_epoch() {
        let epoch = time_method("epoch", &[]);
        assert!(matches!(epoch, Value::Int(n) if n > 1_700_000_000));
    }

    #[test]
    fn time_today() {
        let today = time_method("today", &[]);
        assert!(matches!(today, Value::String(s) if s.starts_with("20")));
    }

    // ── Duration ────────────────────────────────────────────────────

    #[test]
    fn duration_hours() {
        assert_eq!(duration_to_seconds(2.0, "hours"), Value::Int(7200));
    }

    #[test]
    fn duration_minutes() {
        assert_eq!(duration_to_seconds(30.0, "minutes"), Value::Int(1800));
    }

    #[test]
    fn duration_days() {
        assert_eq!(duration_to_seconds(1.0, "days"), Value::Int(86400));
    }

    // ── JSON ────────────────────────────────────────────────────────

    #[test]
    fn parse_json() {
        let val = parse_json_to_value(r#"{"name": "rush", "version": 1}"#);
        if let Value::Hash(map) = val {
            assert_eq!(map.get("name"), Some(&Value::String("rush".to_string())));
            assert_eq!(map.get("version"), Some(&Value::Int(1)));
        } else {
            panic!("expected hash");
        }
    }

    // ── Env ─────────────────────────────────────────────────────────

    #[test]
    fn env_get_path() {
        let path = env_get("PATH");
        assert!(matches!(path, Value::String(s) if !s.is_empty()));
    }

    // ── Ssh ─────────────────────────────────────────────────────────

    #[test]
    fn ssh_run_returns_hash() {
        // This test uses a nonexistent host to verify the return structure
        let result = ssh_method("run", &[
            Value::String("nonexistent.host.test".to_string()),
            Value::String("echo hello".to_string()),
        ]);
        if let Value::Hash(map) = result {
            assert!(map.contains_key("status"));
            assert!(map.contains_key("exit_code"));
            assert!(map.contains_key("stdout"));
            assert!(map.contains_key("stderr"));
            assert!(map.contains_key("host"));
            assert_eq!(map["status"], Value::String("error".to_string()));
        } else {
            panic!("Ssh.run should return a hash");
        }
    }

    #[test]
    fn ssh_run_missing_args() {
        let result = ssh_method("run", &[]);
        assert_eq!(result, Value::Nil);
    }

    #[test]
    fn ssh_test_unreachable() {
        // ConnectTimeout=5, so this won't hang
        let result = ssh_method("test", &[Value::String("nonexistent.host.test".to_string())]);
        assert_eq!(result, Value::Bool(false));
    }

    // ── Bastion / hop chain support (#208) ─────────────────────────────

    #[test]
    fn parse_chain_single_host() {
        let v = Value::String("host1".to_string());
        assert_eq!(parse_chain(Some(&v)).unwrap(), vec!["host1"]);
    }

    #[test]
    fn parse_chain_comma_string() {
        let v = Value::String("bastion,target".to_string());
        assert_eq!(parse_chain(Some(&v)).unwrap(), vec!["bastion", "target"]);
    }

    #[test]
    fn parse_chain_multi_hop_string() {
        let v = Value::String("a,b,c,d".to_string());
        assert_eq!(parse_chain(Some(&v)).unwrap(), vec!["a", "b", "c", "d"]);
    }

    #[test]
    fn parse_chain_array() {
        let v = Value::Array(vec![
            Value::String("bastion".to_string()),
            Value::String("target".to_string()),
        ]);
        assert_eq!(parse_chain(Some(&v)).unwrap(), vec!["bastion", "target"]);
    }

    #[test]
    fn parse_chain_strips_whitespace_and_empties() {
        let v = Value::String("  a , , b ,c ".to_string());
        assert_eq!(parse_chain(Some(&v)).unwrap(), vec!["a", "b", "c"]);
    }

    #[test]
    fn parse_chain_rejects_empty() {
        let v = Value::String(",,,".to_string());
        assert!(parse_chain(Some(&v)).is_err());
    }

    #[test]
    fn parse_chain_rejects_missing() {
        assert!(parse_chain(None).is_err());
    }

    #[test]
    fn parse_chain_rejects_bad_type() {
        assert!(parse_chain(Some(&Value::Int(42))).is_err());
    }

    #[test]
    fn build_ssh_args_single_host() {
        let hops = vec!["target".to_string()];
        let opts = ["-o", "BatchMode=yes"];
        let args = build_ssh_args(&hops, &opts);
        assert_eq!(args, vec!["-o", "BatchMode=yes", "target"]);
    }

    #[test]
    fn build_ssh_args_single_bastion() {
        let hops = vec!["bastion".to_string(), "target".to_string()];
        let opts = ["-o", "BatchMode=yes"];
        let args = build_ssh_args(&hops, &opts);
        assert_eq!(args, vec!["-o", "BatchMode=yes", "-J", "bastion", "target"]);
    }

    #[test]
    fn build_ssh_args_multi_hop() {
        let hops = vec![
            "bastion".to_string(),
            "intermediate".to_string(),
            "target".to_string(),
        ];
        let opts: [&str; 0] = [];
        let args = build_ssh_args(&hops, &opts);
        assert_eq!(args, vec!["-J", "bastion,intermediate", "target"]);
    }

    #[test]
    fn ssh_run_chain_returns_chain_in_hash() {
        // Use unreachable hosts so we exercise the structure without
        // network dependencies.
        let result = ssh_method("run", &[
            Value::String("bastion.test,target.test".to_string()),
            Value::String("echo x".to_string()),
        ]);
        if let Value::Hash(map) = result {
            assert!(map.contains_key("chain"));
            assert!(map.contains_key("chain_str"));
            assert_eq!(map["chain_str"], Value::String("bastion.test,target.test".to_string()));
            // host should be the final destination, not the bastion
            assert_eq!(map["host"], Value::String("target.test".to_string()));
            if let Value::Array(c) = &map["chain"] {
                assert_eq!(c.len(), 2);
            } else {
                panic!("chain should be an array");
            }
        } else {
            panic!("Ssh.run should return a hash");
        }
    }

    #[test]
    fn ssh_run_array_chain_form_works() {
        let result = ssh_method("run", &[
            Value::Array(vec![
                Value::String("a.test".to_string()),
                Value::String("b.test".to_string()),
                Value::String("c.test".to_string()),
            ]),
            Value::String("echo x".to_string()),
        ]);
        if let Value::Hash(map) = result {
            assert_eq!(map["host"], Value::String("c.test".to_string()));
            if let Value::Array(c) = &map["chain"] {
                assert_eq!(c.len(), 3);
            } else {
                panic!("chain should be an array");
            }
        } else {
            panic!("Ssh.run should return a hash");
        }
    }

    #[test]
    fn ssh_test_chain_form_does_not_panic() {
        // Sanity: chain form passes through Ssh.test without crashing.
        // Returns false because the hosts don't exist.
        let result = ssh_method("test", &[
            Value::String("bastion.test,target.test".to_string()),
        ]);
        assert_eq!(result, Value::Bool(false));
    }
}
