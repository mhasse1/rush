//! Rush stdlib: File, Dir, Time — native implementations (no PowerShell).

use std::collections::HashMap;
use std::fs;
use std::path::Path;
use std::time::SystemTime;

use crate::value::Value;

// ── File ────────────────────────────────────────────────────────────

pub fn file_method(method: &str, args: &[Value]) -> Value {
    match method {
        "read" => {
            let path = arg_str(args, 0);
            match fs::read_to_string(&path) {
                Ok(content) => Value::String(content),
                Err(e) => {
                    eprintln!("File.read: {path}: {e}");
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
                    eprintln!("File.read_lines: {path}: {e}");
                    Value::Array(Vec::new())
                }
            }
        }
        "read_json" => {
            let path = arg_str(args, 0);
            match fs::read_to_string(&path) {
                Ok(content) => parse_json_to_value(&content),
                Err(e) => {
                    eprintln!("File.read_json: {path}: {e}");
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
                    eprintln!("File.write: {path}: {e}");
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
                            eprintln!("File.append: {path}: {e}");
                            Value::Bool(false)
                        }
                    }
                }
                Err(e) => {
                    eprintln!("File.append: {path}: {e}");
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
                    eprintln!("File.delete: {path}: {e}");
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
                    eprintln!("File.copy: {src} → {dst}: {e}");
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
                    eprintln!("File.move: {src} → {dst}: {e}");
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
                    eprintln!("Dir.mkdir: {path}: {e}");
                    Value::Bool(false)
                }
            }
        }
        "rmdir" | "delete" => {
            let path = arg_str(args, 0);
            match fs::remove_dir_all(&path) {
                Ok(()) => Value::Bool(true),
                Err(e) => {
                    eprintln!("Dir.rmdir: {path}: {e}");
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
                    eprintln!("Dir.glob: {e}");
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
        entries.push(Value::String(name.clone()));
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
        use std::ffi::CStr;
        unsafe {
            let t = epoch_secs as libc::time_t;
            let mut tm: libc::tm = std::mem::zeroed();
            libc::localtime_r(&t, &mut tm);
            let mut buf = [0u8; 64];
            let fmt = CStr::from_bytes_with_nul_unchecked(b"%Y-%m-%d %H:%M:%S\0");
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
        assert_eq!(file_method("basename", &[p.clone()]), Value::String("rush.exe".to_string()));
        assert_eq!(file_method("dirname", &[p.clone()]), Value::String("/usr/local/bin".to_string()));
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
}
