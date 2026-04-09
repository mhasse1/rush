//! Rush pipeline operators: where, select, sort, count, first, last, skip,
//! sum, avg, min, max, distinct, as json/csv, from json/csv, objectify, tee, grep.
//!
//! Pipeline operators transform Rush values (typically arrays of strings or hashes)
//! flowing through a `|` chain.

use std::collections::HashMap;
use crate::value::Value;

/// A parsed pipeline operator with its arguments.
#[derive(Debug)]
pub struct PipeOp {
    pub name: String,
    pub args: Vec<String>,
}

/// Known pipeline operator names.
/// NOTE: grep, head, tail are intentionally NOT here — they are native
/// Unix commands that users expect to work in pipes. Rush's structured
/// pipeline ops are only the ones that don't collide with real commands.
const PIPE_OPS: &[&str] = &[
    "where", "select", "sort", "count", "first", "last", "skip",
    "sum", "avg", "min", "max", "distinct", "uniq", "reverse",
    "as", "from", "objectify", "tee",
    "each", "times", "columns", "json",
];

/// Check if a word is a pipeline operator.
pub fn is_pipe_op(word: &str) -> bool {
    PIPE_OPS.iter().any(|op| op.eq_ignore_ascii_case(word))
}

/// Split a pipeline string into segments on `|`.
/// Respects quoting so `echo "a | b"` doesn't split.
pub fn split_pipeline(line: &str) -> Vec<String> {
    let mut segments = Vec::new();
    let mut current = String::new();
    let mut in_single = false;
    let mut in_double = false;

    for ch in line.chars() {
        match ch {
            '\'' if !in_double => in_single = !in_single,
            '"' if !in_single => in_double = !in_double,
            '|' if !in_single && !in_double => {
                segments.push(current.trim().to_string());
                current.clear();
                continue;
            }
            _ => {}
        }
        current.push(ch);
    }
    if !current.trim().is_empty() {
        segments.push(current.trim().to_string());
    }
    segments
}

/// Parse a pipeline operator segment into name + args.
pub fn parse_pipe_op(segment: &str) -> PipeOp {
    let parts: Vec<String> = shell_split(segment);
    let name = parts.first().map(|s| s.to_lowercase()).unwrap_or_default();
    let args = parts.into_iter().skip(1).collect();
    PipeOp { name, args }
}

/// Apply a pipeline operator to a value.
pub fn apply_pipe_op(input: Value, op: &PipeOp) -> Value {
    match op.name.as_str() {
        "where" => apply_where(input, &op.args),
        "select" => apply_select(input, &op.args),
        "sort" => apply_sort(input, &op.args),
        "count" => apply_count(input),
        "first" | "head" => apply_first(input, &op.args),
        "last" | "tail" => apply_last(input, &op.args),
        "skip" => apply_skip(input, &op.args),
        "sum" => apply_sum(input, &op.args),
        "avg" => apply_avg(input, &op.args),
        "min" => apply_min(input, &op.args),
        "max" => apply_max(input, &op.args),
        "distinct" | "uniq" => apply_distinct(input),
        "reverse" => apply_reverse(input),
        "as" => apply_as(input, &op.args),
        "from" | "json" => apply_from(input, &op.args),
        "objectify" => apply_objectify(input),
        "grep" => apply_grep(input, &op.args),
        "tee" => apply_tee(input, &op.args),
        "columns" => apply_columns(input, &op.args),
        _ => input,
    }
}

/// Convert text stdout into an array of lines for pipeline processing.
pub fn text_to_array(text: &str) -> Value {
    Value::Array(
        text.lines()
            .map(|l| Value::String(l.to_string()))
            .collect(),
    )
}

// ── Operator Implementations ────────────────────────────────────────

fn apply_where(input: Value, args: &[String]) -> Value {
    let items = to_items(input);
    if args.is_empty() {
        return Value::Array(items);
    }

    // Simple: where field op value (e.g., where size > 1000)
    // Or: where /pattern/ (regex match on string repr)
    let condition = args.join(" ");

    // Regex filter: where /pattern/
    if condition.starts_with('/') && condition.ends_with('/') && condition.len() > 2 {
        let pattern = &condition[1..condition.len() - 1];
        let filtered: Vec<Value> = items
            .into_iter()
            .filter(|v| v.to_rush_string().contains(pattern))
            .collect();
        return Value::Array(filtered);
    }

    // Field comparison: where field op value
    if args.len() >= 3 {
        let field = &args[0];
        let op = &args[1];
        let val = &args[2..].join(" ");

        let filtered: Vec<Value> = items
            .into_iter()
            .filter(|item| {
                let item_val = get_field(item, field);
                compare_values(&item_val, op, val)
            })
            .collect();
        return Value::Array(filtered);
    }

    // String contains filter: where pattern
    let pattern = &condition;
    let filtered: Vec<Value> = items
        .into_iter()
        .filter(|v| {
            v.to_rush_string()
                .to_lowercase()
                .contains(&pattern.to_lowercase())
        })
        .collect();
    Value::Array(filtered)
}

fn apply_select(input: Value, args: &[String]) -> Value {
    let items = to_items(input);
    if args.is_empty() {
        return Value::Array(items);
    }

    // select field1, field2, ... — project specific fields
    let fields: Vec<&str> = args
        .iter()
        .flat_map(|a| a.split(','))
        .map(|s| s.trim())
        .filter(|s| !s.is_empty())
        .collect();

    let projected: Vec<Value> = items
        .into_iter()
        .map(|item| {
            if fields.len() == 1 {
                get_field(&item, fields[0])
            } else {
                let mut map = HashMap::new();
                for f in &fields {
                    map.insert(f.to_string(), get_field(&item, f));
                }
                Value::Hash(map)
            }
        })
        .collect();
    Value::Array(projected)
}

fn apply_sort(input: Value, args: &[String]) -> Value {
    let mut items = to_items(input);

    if let Some(field) = args.first() {
        // Sort by field
        items.sort_by(|a, b| {
            let va = get_field(a, field);
            let vb = get_field(b, field);
            va.partial_cmp(&vb).unwrap_or(std::cmp::Ordering::Equal)
        });
    } else {
        items.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    }

    // Check for --desc / -r flag
    if args.iter().any(|a| a == "--desc" || a == "-r") {
        items.reverse();
    }

    Value::Array(items)
}

fn apply_count(input: Value) -> Value {
    match input {
        Value::Array(arr) => Value::Int(arr.len() as i64),
        Value::String(s) => Value::Int(s.lines().count() as i64),
        _ => Value::Int(1),
    }
}

fn apply_first(input: Value, args: &[String]) -> Value {
    let n: usize = args.first().and_then(|s| s.parse().ok()).unwrap_or(1);
    let items = to_items(input);
    if n == 1 {
        items.into_iter().next().unwrap_or(Value::Nil)
    } else {
        Value::Array(items.into_iter().take(n).collect())
    }
}

fn apply_last(input: Value, args: &[String]) -> Value {
    let n: usize = args.first().and_then(|s| s.parse().ok()).unwrap_or(1);
    let items = to_items(input);
    let skip = items.len().saturating_sub(n);
    if n == 1 {
        items.into_iter().last().unwrap_or(Value::Nil)
    } else {
        Value::Array(items.into_iter().skip(skip).collect())
    }
}

fn apply_skip(input: Value, args: &[String]) -> Value {
    let n: usize = args.first().and_then(|s| s.parse().ok()).unwrap_or(0);
    let items = to_items(input);
    Value::Array(items.into_iter().skip(n).collect())
}

fn apply_sum(input: Value, args: &[String]) -> Value {
    let items = to_items(input);
    if let Some(field) = args.first() {
        // Sum a specific field
        let mut total = 0.0_f64;
        for item in items {
            if let Some(n) = get_field(&item, field).to_float() {
                total += n;
            }
        }
        if total == total.floor() {
            Value::Int(total as i64)
        } else {
            Value::Float(total)
        }
    } else {
        // Sum the values directly
        let mut total = 0.0_f64;
        for item in items {
            if let Some(n) = item.to_float() {
                total += n;
            }
        }
        if total == total.floor() {
            Value::Int(total as i64)
        } else {
            Value::Float(total)
        }
    }
}

fn apply_avg(input: Value, args: &[String]) -> Value {
    let items = to_items(input);
    let count = items.len();
    if count == 0 {
        return Value::Nil;
    }
    let sum = apply_sum(Value::Array(items), args);
    match sum {
        Value::Int(n) => Value::Float(n as f64 / count as f64),
        Value::Float(f) => Value::Float(f / count as f64),
        _ => Value::Nil,
    }
}

fn apply_min(input: Value, args: &[String]) -> Value {
    let items = to_items(input);
    if let Some(field) = args.first() {
        items
            .into_iter()
            .min_by(|a, b| {
                let va = get_field(a, field);
                let vb = get_field(b, field);
                va.partial_cmp(&vb).unwrap_or(std::cmp::Ordering::Equal)
            })
            .unwrap_or(Value::Nil)
    } else {
        items
            .into_iter()
            .min_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal))
            .unwrap_or(Value::Nil)
    }
}

fn apply_max(input: Value, args: &[String]) -> Value {
    let items = to_items(input);
    if let Some(field) = args.first() {
        items
            .into_iter()
            .max_by(|a, b| {
                let va = get_field(a, field);
                let vb = get_field(b, field);
                va.partial_cmp(&vb).unwrap_or(std::cmp::Ordering::Equal)
            })
            .unwrap_or(Value::Nil)
    } else {
        items
            .into_iter()
            .max_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal))
            .unwrap_or(Value::Nil)
    }
}

fn apply_distinct(input: Value) -> Value {
    let items = to_items(input);
    let mut seen = Vec::new();
    let mut result = Vec::new();
    for item in items {
        let key = item.inspect();
        if !seen.contains(&key) {
            seen.push(key);
            result.push(item);
        }
    }
    Value::Array(result)
}

fn apply_reverse(input: Value) -> Value {
    let mut items = to_items(input);
    items.reverse();
    Value::Array(items)
}

fn apply_as(input: Value, args: &[String]) -> Value {
    let format = args.first().map(|s| s.to_lowercase()).unwrap_or_default();
    match format.as_str() {
        "json" => {
            let json = value_to_json(&input);
            Value::String(
                serde_json::to_string_pretty(&json).unwrap_or_else(|_| "null".to_string()),
            )
        }
        "csv" => {
            let items = to_items(input);
            Value::String(values_to_csv(&items))
        }
        _ => input,
    }
}

fn apply_from(input: Value, args: &[String]) -> Value {
    let format = args.first().map(|s| s.to_lowercase()).unwrap_or_else(|| "json".to_string());
    let text = match &input {
        Value::String(s) => s.clone(),
        _ => input.to_rush_string(),
    };
    match format.as_str() {
        "json" => {
            match serde_json::from_str::<serde_json::Value>(&text) {
                Ok(v) => json_to_value(&v),
                Err(_) => input,
            }
        }
        "csv" => csv_to_values(&text),
        _ => input,
    }
}

fn apply_objectify(input: Value) -> Value {
    // If already objectified (array of hashes), pass through
    if let Value::Array(ref arr) = input {
        if arr.first().map_or(false, |v| matches!(v, Value::Hash(_))) {
            return input;
        }
    }

    // Convert tabular text output to array of hashes.
    let text = match &input {
        Value::String(s) => s.clone(),
        Value::Array(arr) => arr.iter().map(|v| v.to_rush_string()).collect::<Vec<_>>().join("\n"),
        _ => return input,
    };

    let lines: Vec<&str> = text.lines().collect();
    if lines.len() < 2 {
        // Header only or empty — return empty array
        return Value::Array(Vec::new());
    }

    let header_line = lines[0];

    // Split headers on whitespace. The last column gets all remaining text
    // (handles COMMAND fields with spaces in ps, docker, etc.).
    let headers: Vec<&str> = header_line.split_whitespace().collect();
    if headers.is_empty() {
        return input;
    }
    let col_count = headers.len();

    let mut objects = Vec::new();
    for line in &lines[1..] {
        if line.trim().is_empty() {
            continue;
        }
        // Split into whitespace-delimited fields.
        // First N-1 fields are individual tokens; last field gets ALL remaining text.
        let fields = split_n_fields(line, col_count);

        let mut map = HashMap::new();
        for (i, header) in headers.iter().enumerate() {
            let val = fields.get(i).map(|s| s.trim()).unwrap_or("");
            if let Ok(n) = val.parse::<i64>() {
                map.insert(header.to_string(), Value::Int(n));
            } else if let Ok(f) = val.parse::<f64>() {
                map.insert(header.to_string(), Value::Float(f));
            } else {
                map.insert(header.to_string(), Value::String(val.to_string()));
            }
        }
        objects.push(Value::Hash(map));
    }

    Value::Array(objects)
}

/// Split a line into N fields. First N-1 fields are whitespace-delimited tokens.
/// The Nth field gets ALL remaining text (preserving spaces — for COMMAND columns).
fn split_n_fields(line: &str, n: usize) -> Vec<String> {
    if n == 0 { return Vec::new(); }
    if n == 1 { return vec![line.trim().to_string()]; }

    let mut fields = Vec::new();
    let mut rest = line;

    for _ in 0..n - 1 {
        let trimmed = rest.trim_start();
        if trimmed.is_empty() {
            fields.push(String::new());
            continue;
        }
        // Find end of this field (next whitespace)
        if let Some(space_pos) = trimmed.find(char::is_whitespace) {
            fields.push(trimmed[..space_pos].to_string());
            rest = &trimmed[space_pos..];
        } else {
            fields.push(trimmed.to_string());
            rest = "";
        }
    }

    // Last field: everything remaining
    fields.push(rest.trim_start().to_string());
    fields
}

fn apply_grep(input: Value, args: &[String]) -> Value {
    let pattern = args.first().map(|s| s.as_str()).unwrap_or("");
    if pattern.is_empty() {
        return input;
    }
    let case_insensitive = args.iter().any(|a| a == "-i");
    let items = to_items(input);
    let filtered: Vec<Value> = items
        .into_iter()
        .filter(|v| {
            let s = v.to_rush_string();
            if case_insensitive {
                s.to_lowercase().contains(&pattern.to_lowercase())
            } else {
                s.contains(pattern)
            }
        })
        .collect();
    Value::Array(filtered)
}

fn apply_tee(input: Value, args: &[String]) -> Value {
    if let Some(path) = args.first() {
        let text = match &input {
            Value::Array(arr) => arr.iter().map(|v| v.to_rush_string()).collect::<Vec<_>>().join("\n"),
            other => other.to_rush_string(),
        };
        if let Err(e) = std::fs::write(path, &text) {
            eprintln!("tee: {path}: {e}");
        }
    } else {
        eprintln!("tee: missing filename");
    }
    input // pass through
}

fn apply_columns(input: Value, args: &[String]) -> Value {
    // columns 1,3,5 — select columns by 1-based index
    let indices: Vec<usize> = args
        .iter()
        .flat_map(|a| a.split(','))
        .filter_map(|s| s.trim().parse::<usize>().ok())
        .map(|i| i.saturating_sub(1)) // 1-based → 0-based
        .collect();

    if indices.is_empty() {
        return input;
    }

    let items = to_items(input);
    let result: Vec<Value> = items
        .into_iter()
        .map(|item| {
            let text = item.to_rush_string();
            let fields: Vec<&str> = text.split_whitespace().collect();
            let selected: Vec<&str> = indices
                .iter()
                .filter_map(|&i| fields.get(i).copied())
                .collect();
            Value::String(selected.join("\t"))
        })
        .collect();
    Value::Array(result)
}

// ── Auto-objectify ──────────────────────────────────────────────────

/// Check if a command should auto-objectify its output for pipeline operators.
/// Uses the objectify config system (built-in defaults + user config).
pub fn should_auto_objectify(first_segment: &str) -> bool {
    crate::objectify_config::get().should_objectify(first_segment)
}

// ── Helpers ─────────────────────────────────────────────────────────

fn to_items(value: Value) -> Vec<Value> {
    match value {
        Value::Array(arr) => arr,
        Value::String(s) => s.lines().map(|l| Value::String(l.to_string())).collect(),
        other => vec![other],
    }
}

fn get_field(item: &Value, field: &str) -> Value {
    match item {
        Value::Hash(map) => map.get(field).cloned().unwrap_or(Value::Nil),
        Value::String(s) => {
            // For string items, try parsing as number if field access is attempted
            Value::String(s.clone())
        }
        _ => item.clone(),
    }
}

fn compare_values(item_val: &Value, op: &str, target: &str) -> bool {
    // Try numeric comparison
    if let (Some(a), Ok(b)) = (item_val.to_float(), target.parse::<f64>()) {
        return match op {
            ">" => a > b,
            ">=" => a >= b,
            "<" => a < b,
            "<=" => a <= b,
            "==" | "=" => (a - b).abs() < f64::EPSILON,
            "!=" => (a - b).abs() >= f64::EPSILON,
            _ => false,
        };
    }

    // String comparison
    let a = item_val.to_rush_string();
    let b = target.trim_matches('"').trim_matches('\'');
    match op {
        "==" | "=" => a == b,
        "!=" => a != b,
        ">" => a > b.to_string(),
        "<" => (a) < b.to_string(),
        ">=" => a >= b.to_string(),
        "<=" => a <= b.to_string(),
        "=~" => a.contains(b),
        "!~" => !a.contains(b),
        _ => false,
    }
}

fn value_to_json(val: &Value) -> serde_json::Value {
    match val {
        Value::Nil => serde_json::Value::Null,
        Value::Bool(b) => serde_json::Value::Bool(*b),
        Value::Int(n) => serde_json::Value::Number((*n).into()),
        Value::Float(f) => {
            serde_json::Number::from_f64(*f)
                .map(serde_json::Value::Number)
                .unwrap_or(serde_json::Value::Null)
        }
        Value::String(s) => serde_json::Value::String(s.clone()),
        Value::Symbol(s) => serde_json::Value::String(s.clone()),
        Value::Array(arr) => {
            serde_json::Value::Array(arr.iter().map(value_to_json).collect())
        }
        Value::Hash(map) => {
            let obj: serde_json::Map<String, serde_json::Value> =
                map.iter().map(|(k, v)| (k.clone(), value_to_json(v))).collect();
            serde_json::Value::Object(obj)
        }
        Value::Range(start, end, exclusive) => {
            let items: Vec<serde_json::Value> = if *exclusive {
                (*start..*end).map(|n| serde_json::Value::Number(n.into())).collect()
            } else {
                (*start..=*end).map(|n| serde_json::Value::Number(n.into())).collect()
            };
            serde_json::Value::Array(items)
        }
    }
}

fn json_to_value(v: &serde_json::Value) -> Value {
    match v {
        serde_json::Value::Null => Value::Nil,
        serde_json::Value::Bool(b) => Value::Bool(*b),
        serde_json::Value::Number(n) => {
            if let Some(i) = n.as_i64() { Value::Int(i) }
            else if let Some(f) = n.as_f64() { Value::Float(f) }
            else { Value::Nil }
        }
        serde_json::Value::String(s) => Value::String(s.clone()),
        serde_json::Value::Array(arr) => Value::Array(arr.iter().map(json_to_value).collect()),
        serde_json::Value::Object(obj) => {
            let mut map = HashMap::new();
            for (k, v) in obj {
                map.insert(k.clone(), json_to_value(v));
            }
            Value::Hash(map)
        }
    }
}

fn values_to_csv(items: &[Value]) -> String {
    if items.is_empty() {
        return String::new();
    }

    // Get headers from first hash item
    if let Some(Value::Hash(first)) = items.first() {
        let headers: Vec<&String> = first.keys().collect();
        let mut result = headers.iter().map(|h| h.as_str()).collect::<Vec<_>>().join(",");
        result.push('\n');

        for item in items {
            if let Value::Hash(map) = item {
                let row: Vec<String> = headers
                    .iter()
                    .map(|h| {
                        let v = map.get(*h).map(|v| v.to_rush_string()).unwrap_or_default();
                        if v.contains(',') || v.contains('"') {
                            format!("\"{}\"", v.replace('"', "\"\""))
                        } else {
                            v
                        }
                    })
                    .collect();
                result.push_str(&row.join(","));
                result.push('\n');
            }
        }
        result
    } else {
        // Array of non-hash items → one column
        items.iter().map(|v| v.to_rush_string()).collect::<Vec<_>>().join("\n")
    }
}

fn csv_to_values(text: &str) -> Value {
    let lines: Vec<&str> = text.lines().collect();
    if lines.len() < 2 {
        return Value::String(text.to_string());
    }

    let headers: Vec<&str> = lines[0].split(',').map(|s| s.trim()).collect();
    let mut rows = Vec::new();

    for line in &lines[1..] {
        if line.trim().is_empty() { continue; }
        let fields: Vec<&str> = line.split(',').map(|s| s.trim()).collect();
        let mut map = HashMap::new();
        for (i, header) in headers.iter().enumerate() {
            let val = fields.get(i).unwrap_or(&"").to_string();
            map.insert(header.to_string(), Value::String(val));
        }
        rows.push(Value::Hash(map));
    }
    Value::Array(rows)
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

    fn arr(vals: &[i64]) -> Value {
        Value::Array(vals.iter().map(|n| Value::Int(*n)).collect())
    }

    fn str_arr(vals: &[&str]) -> Value {
        Value::Array(vals.iter().map(|s| Value::String(s.to_string())).collect())
    }

    #[test]
    fn pipe_split() {
        assert_eq!(split_pipeline("ls | grep foo | count"), vec!["ls", "grep foo", "count"]);
        assert_eq!(split_pipeline("echo \"a | b\""), vec!["echo \"a | b\""]);
    }

    #[test]
    fn op_count() {
        assert_eq!(apply_pipe_op(arr(&[1, 2, 3]), &parse_pipe_op("count")), Value::Int(3));
    }

    #[test]
    fn op_first() {
        assert_eq!(apply_pipe_op(arr(&[10, 20, 30]), &parse_pipe_op("first")), Value::Int(10));
        assert_eq!(apply_pipe_op(arr(&[10, 20, 30]), &parse_pipe_op("first 2")), arr(&[10, 20]));
    }

    #[test]
    fn op_last() {
        assert_eq!(apply_pipe_op(arr(&[10, 20, 30]), &parse_pipe_op("last")), Value::Int(30));
        assert_eq!(apply_pipe_op(arr(&[10, 20, 30]), &parse_pipe_op("last 2")), arr(&[20, 30]));
    }

    #[test]
    fn op_skip() {
        assert_eq!(apply_pipe_op(arr(&[1, 2, 3, 4]), &parse_pipe_op("skip 2")), arr(&[3, 4]));
    }

    #[test]
    fn op_sort() {
        assert_eq!(apply_pipe_op(arr(&[3, 1, 2]), &parse_pipe_op("sort")), arr(&[1, 2, 3]));
    }

    #[test]
    fn op_reverse() {
        assert_eq!(apply_pipe_op(arr(&[1, 2, 3]), &parse_pipe_op("reverse")), arr(&[3, 2, 1]));
    }

    #[test]
    fn op_distinct() {
        assert_eq!(apply_pipe_op(arr(&[1, 2, 2, 3, 1]), &parse_pipe_op("distinct")), arr(&[1, 2, 3]));
    }

    #[test]
    fn op_sum() {
        assert_eq!(apply_pipe_op(arr(&[1, 2, 3, 4]), &parse_pipe_op("sum")), Value::Int(10));
    }

    #[test]
    fn op_avg() {
        assert_eq!(apply_pipe_op(arr(&[2, 4, 6]), &parse_pipe_op("avg")), Value::Float(4.0));
    }

    #[test]
    fn op_min_max() {
        assert_eq!(apply_pipe_op(arr(&[3, 1, 2]), &parse_pipe_op("min")), Value::Int(1));
        assert_eq!(apply_pipe_op(arr(&[3, 1, 2]), &parse_pipe_op("max")), Value::Int(3));
    }

    #[test]
    fn op_where_string() {
        let input = str_arr(&["hello world", "goodbye", "hello rust"]);
        let result = apply_pipe_op(input, &parse_pipe_op("where hello"));
        assert_eq!(result, str_arr(&["hello world", "hello rust"]));
    }

    #[test]
    fn op_where_regex() {
        let input = str_arr(&["ERROR: bad", "INFO: good", "ERROR: worse"]);
        let result = apply_pipe_op(input, &parse_pipe_op("where /ERROR/"));
        assert_eq!(result, str_arr(&["ERROR: bad", "ERROR: worse"]));
    }

    #[test]
    fn op_grep() {
        let input = str_arr(&["foo bar", "baz qux", "foo baz"]);
        let result = apply_pipe_op(input, &parse_pipe_op("grep foo"));
        assert_eq!(result, str_arr(&["foo bar", "foo baz"]));
    }

    #[test]
    fn op_as_json() {
        let result = apply_pipe_op(arr(&[1, 2, 3]), &parse_pipe_op("as json"));
        if let Value::String(s) = result {
            assert!(s.contains("["));
            assert!(s.contains("1"));
        } else {
            panic!("expected string");
        }
    }

    #[test]
    fn op_from_json() {
        let input = Value::String("[1, 2, 3]".to_string());
        let result = apply_pipe_op(input, &parse_pipe_op("from json"));
        assert_eq!(result, arr(&[1, 2, 3]));
    }

    #[test]
    fn op_select_single_field() {
        let items = Value::Array(vec![
            Value::Hash(HashMap::from([
                ("name".to_string(), Value::String("Alice".to_string())),
                ("age".to_string(), Value::Int(30)),
            ])),
            Value::Hash(HashMap::from([
                ("name".to_string(), Value::String("Bob".to_string())),
                ("age".to_string(), Value::Int(25)),
            ])),
        ]);
        let result = apply_pipe_op(items, &parse_pipe_op("select name"));
        assert_eq!(result, str_arr(&["Alice", "Bob"]));
    }

    #[test]
    fn op_where_field_comparison() {
        let items = Value::Array(vec![
            Value::Hash(HashMap::from([
                ("name".to_string(), Value::String("Alice".to_string())),
                ("age".to_string(), Value::Int(30)),
            ])),
            Value::Hash(HashMap::from([
                ("name".to_string(), Value::String("Bob".to_string())),
                ("age".to_string(), Value::Int(25)),
            ])),
        ]);
        let result = apply_pipe_op(items, &parse_pipe_op("where age > 27"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 1);
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn op_objectify() {
        let text = Value::String("NAME  AGE\nAlice 30\nBob   25".to_string());
        let result = apply_pipe_op(text, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 2);
            if let Value::Hash(first) = &arr[0] {
                assert_eq!(first.get("NAME"), Some(&Value::String("Alice".to_string())));
                assert_eq!(first.get("AGE"), Some(&Value::Int(30)));
            }
            if let Value::Hash(second) = &arr[1] {
                assert_eq!(second.get("NAME"), Some(&Value::String("Bob".to_string())));
                assert_eq!(second.get("AGE"), Some(&Value::Int(25)));
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_last_column_preserves_spaces() {
        // Like ps aux: COMMAND column contains spaces
        let text = Value::String(
            "USER PID COMMAND\nmark 123 /usr/bin/some app --flag\nroot 1 /sbin/init".to_string()
        );
        let result = apply_pipe_op(text, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 2);
            if let Value::Hash(first) = &arr[0] {
                assert_eq!(first.get("USER"), Some(&Value::String("mark".to_string())));
                assert_eq!(first.get("PID"), Some(&Value::Int(123)));
                assert_eq!(first.get("COMMAND"), Some(&Value::String("/usr/bin/some app --flag".to_string())));
            }
            if let Value::Hash(second) = &arr[1] {
                assert_eq!(second.get("USER"), Some(&Value::String("root".to_string())));
                assert_eq!(second.get("PID"), Some(&Value::Int(1)));
                assert_eq!(second.get("COMMAND"), Some(&Value::String("/sbin/init".to_string())));
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_numeric_parsing() {
        let text = Value::String("NAME SCORE RATIO\nAlice 95 3.14\nBob 87 2.71".to_string());
        let result = apply_pipe_op(text, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            if let Value::Hash(first) = &arr[0] {
                assert_eq!(first.get("SCORE"), Some(&Value::Int(95)));
                assert_eq!(first.get("RATIO"), Some(&Value::Float(3.14)));
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_empty_fields() {
        // Some rows may have fewer fields than headers
        let text = Value::String("A B C\n1 2 3\n4 5\n6".to_string());
        let result = apply_pipe_op(text, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 3);
            // Third row has only one field
            if let Value::Hash(third) = &arr[2] {
                assert_eq!(third.get("A"), Some(&Value::Int(6)));
                // B and C should be empty strings
                assert_eq!(third.get("B"), Some(&Value::String(String::new())));
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_skips_blank_lines() {
        let text = Value::String("NAME AGE\nAlice 30\n\nBob 25\n  \nCarol 28".to_string());
        let result = apply_pipe_op(text, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 3, "should skip blank lines: got {}", arr.len());
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_single_column() {
        let text = Value::String("NAME\nAlice\nBob\nCarol".to_string());
        let result = apply_pipe_op(text, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 3);
            if let Value::Hash(first) = &arr[0] {
                assert_eq!(first.get("NAME"), Some(&Value::String("Alice".to_string())));
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_idempotent() {
        // If input is already objectified (array of hashes), pass through
        let hash1 = Value::Hash({
            let mut m = HashMap::new();
            m.insert("name".to_string(), Value::String("Alice".to_string()));
            m
        });
        let input = Value::Array(vec![hash1]);
        let result = apply_pipe_op(input.clone(), &parse_pipe_op("objectify"));
        assert_eq!(result, input, "objectify should be idempotent on array of hashes");
    }

    #[test]
    fn objectify_header_only() {
        // Just a header, no data
        let text = Value::String("NAME AGE SCORE".to_string());
        let result = apply_pipe_op(text, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 0, "header-only should produce empty array");
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_from_array_input() {
        // Input as array of strings (like text_to_array produces)
        let input = Value::Array(vec![
            Value::String("NAME AGE".to_string()),
            Value::String("Alice 30".to_string()),
            Value::String("Bob 25".to_string()),
        ]);
        let result = apply_pipe_op(input, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 2);
            if let Value::Hash(first) = &arr[0] {
                assert_eq!(first.get("NAME"), Some(&Value::String("Alice".to_string())));
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_many_columns() {
        // Like df -h output
        let text = Value::String(
            "Filesystem Size Used Avail Capacity Mounted on\n/dev/disk1 926G 600G 326G 65% /".to_string()
        );
        let result = apply_pipe_op(text, &parse_pipe_op("objectify"));
        if let Value::Array(arr) = &result {
            assert_eq!(arr.len(), 1);
            if let Value::Hash(row) = &arr[0] {
                assert_eq!(row.get("Filesystem"), Some(&Value::String("/dev/disk1".to_string())));
                assert_eq!(row.get("Size"), Some(&Value::String("926G".to_string())));
                assert_eq!(row.get("Capacity"), Some(&Value::String("65%".to_string())));
                // "Mounted on" is two words but our splitter treats "on" as separate column
                // This is a known limitation — single-word headers only
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_then_where() {
        // End-to-end: objectify → where filter
        let text = Value::String("NAME AGE\nAlice 30\nBob 25\nCarol 35".to_string());
        let objectified = apply_pipe_op(text, &parse_pipe_op("objectify"));
        let filtered = apply_pipe_op(objectified, &parse_pipe_op("where AGE > 28"));
        if let Value::Array(arr) = &filtered {
            assert_eq!(arr.len(), 2, "should have 2 rows with AGE > 28");
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_then_select() {
        let text = Value::String("NAME AGE SCORE\nAlice 30 95\nBob 25 87".to_string());
        let objectified = apply_pipe_op(text, &parse_pipe_op("objectify"));
        let selected = apply_pipe_op(objectified, &parse_pipe_op("select NAME, SCORE"));
        if let Value::Array(arr) = &selected {
            assert_eq!(arr.len(), 2);
            if let Value::Hash(first) = &arr[0] {
                assert!(first.contains_key("NAME"));
                assert!(first.contains_key("SCORE"));
                assert!(!first.contains_key("AGE"), "AGE should be excluded by select");
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn objectify_then_sort() {
        let text = Value::String("NAME AGE\nCarol 35\nAlice 30\nBob 25".to_string());
        let objectified = apply_pipe_op(text, &parse_pipe_op("objectify"));
        let sorted = apply_pipe_op(objectified, &parse_pipe_op("sort AGE"));
        if let Value::Array(arr) = &sorted {
            assert_eq!(arr.len(), 3);
            if let Value::Hash(first) = &arr[0] {
                assert_eq!(first.get("AGE"), Some(&Value::Int(25)));
            }
            if let Value::Hash(last) = &arr[2] {
                assert_eq!(last.get("AGE"), Some(&Value::Int(35)));
            }
        } else {
            panic!("expected array");
        }
    }

    #[test]
    fn text_to_array_conversion() {
        let result = text_to_array("line1\nline2\nline3");
        assert_eq!(result, str_arr(&["line1", "line2", "line3"]));
    }

    // ── Tee ─────────────────────────────────────────────────────────

    #[test]
    fn op_tee() {
        let tmp = std::env::temp_dir().join("rush_tee_test.txt");
        let path = tmp.to_string_lossy().to_string();
        let input = str_arr(&["hello", "world"]);
        let result = apply_pipe_op(input.clone(), &parse_pipe_op(&format!("tee {path}")));
        // Tee passes through unchanged
        assert_eq!(result, input);
        // And writes to file
        let content = std::fs::read_to_string(&tmp).unwrap();
        assert_eq!(content, "hello\nworld");
        std::fs::remove_file(&tmp).ok();
    }

    // ── Columns ─────────────────────────────────────────────────────

    #[test]
    fn op_columns() {
        let input = str_arr(&["alice 30 eng", "bob 25 sales"]);
        let result = apply_pipe_op(input, &parse_pipe_op("columns 1,3"));
        assert_eq!(result, str_arr(&["alice\teng", "bob\tsales"]));
    }

    // ── Auto-objectify detection ────────────────────────────────────

    #[test]
    fn auto_objectify_detection() {
        assert!(should_auto_objectify("ps aux"));
        assert!(should_auto_objectify("docker ps"));
        assert!(should_auto_objectify("df -h"));
        assert!(!should_auto_objectify("ls -la"));
        assert!(!should_auto_objectify("echo hello"));
    }

    // ── CSV round-trip ──────────────────────────────────────────────

    #[test]
    fn csv_round_trip() {
        let items = Value::Array(vec![
            Value::Hash(HashMap::from([
                ("name".to_string(), Value::String("Alice".to_string())),
                ("age".to_string(), Value::String("30".to_string())),
            ])),
            Value::Hash(HashMap::from([
                ("name".to_string(), Value::String("Bob".to_string())),
                ("age".to_string(), Value::String("25".to_string())),
            ])),
        ]);
        let csv = apply_pipe_op(items.clone(), &parse_pipe_op("as csv"));
        if let Value::String(csv_text) = csv {
            assert!(csv_text.contains("Alice"));
            assert!(csv_text.contains("Bob"));
            // Parse back
            let parsed = apply_pipe_op(Value::String(csv_text), &parse_pipe_op("from csv"));
            if let Value::Array(arr) = parsed {
                assert_eq!(arr.len(), 2);
            } else {
                panic!("expected array from CSV parse");
            }
        } else {
            panic!("expected string from as csv");
        }
    }

    // ── Chained pipeline ────────────────────────────────────────────

    #[test]
    fn chained_pipeline() {
        // Simulate: [5,3,1,4,2] | sort | first 3 | sum
        let input = arr(&[5, 3, 1, 4, 2]);
        let sorted = apply_pipe_op(input, &parse_pipe_op("sort"));
        let first3 = apply_pipe_op(sorted, &parse_pipe_op("first 3"));
        let sum = apply_pipe_op(first3, &parse_pipe_op("sum"));
        assert_eq!(sum, Value::Int(6)); // 1 + 2 + 3
    }
}
