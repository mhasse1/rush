use std::collections::HashMap;
use std::fmt;

/// A Rush runtime value.
#[derive(Debug, Clone)]
pub enum Value {
    Nil,
    Bool(bool),
    Int(i64),
    Float(f64),
    String(String),
    Symbol(String),
    Array(Vec<Value>),
    Hash(HashMap<String, Value>),
    /// A range (start, end, exclusive)
    Range(i64, i64, bool),
}

impl Value {
    /// Truthiness: nil and false are falsy, everything else is truthy.
    pub fn is_truthy(&self) -> bool {
        !matches!(self, Value::Nil | Value::Bool(false))
    }

    /// Coerce to string for interpolation / display.
    pub fn to_rush_string(&self) -> String {
        match self {
            Value::Nil => "".to_string(),
            Value::Bool(b) => b.to_string(),
            Value::Int(n) => n.to_string(),
            Value::Float(f) => {
                if *f == f.floor() && f.is_finite() {
                    format!("{f:.1}")
                } else {
                    f.to_string()
                }
            }
            Value::String(s) => s.clone(),
            Value::Symbol(s) => s.clone(),
            Value::Array(arr) => {
                let items: Vec<String> = arr.iter().map(|v| v.inspect()).collect();
                format!("[{}]", items.join(", "))
            }
            Value::Hash(map) => {
                let items: Vec<String> = map
                    .iter()
                    .map(|(k, v)| format!("{k}: {}", v.inspect()))
                    .collect();
                format!("{{{}}}", items.join(", "))
            }
            Value::Range(start, end, exclusive) => {
                let dots = if *exclusive { "..." } else { ".." };
                format!("{start}{dots}{end}")
            }
        }
    }

    /// Inspect representation (shows quotes around strings, etc.)
    pub fn inspect(&self) -> String {
        match self {
            Value::String(s) => format!("\"{s}\""),
            other => other.to_rush_string(),
        }
    }

    /// Coerce to i64 if possible.
    pub fn to_int(&self) -> Option<i64> {
        match self {
            Value::Int(n) => Some(*n),
            Value::Float(f) => Some(*f as i64),
            Value::String(s) => s.parse().ok(),
            Value::Bool(true) => Some(1),
            Value::Bool(false) => Some(0),
            _ => None,
        }
    }

    /// Coerce to f64 if possible.
    pub fn to_float(&self) -> Option<f64> {
        match self {
            Value::Float(f) => Some(*f),
            Value::Int(n) => Some(*n as f64),
            Value::String(s) => s.parse().ok(),
            _ => None,
        }
    }

    /// Type name for error messages.
    pub fn type_name(&self) -> &'static str {
        match self {
            Value::Nil => "nil",
            Value::Bool(_) => "bool",
            Value::Int(_) => "int",
            Value::Float(_) => "float",
            Value::String(_) => "string",
            Value::Symbol(_) => "symbol",
            Value::Array(_) => "array",
            Value::Hash(_) => "hash",
            Value::Range(..) => "range",
        }
    }
}

impl fmt::Display for Value {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.to_rush_string())
    }
}

impl PartialEq for Value {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (Value::Nil, Value::Nil) => true,
            (Value::Bool(a), Value::Bool(b)) => a == b,
            (Value::Int(a), Value::Int(b)) => a == b,
            (Value::Float(a), Value::Float(b)) => a == b,
            (Value::Int(a), Value::Float(b)) | (Value::Float(b), Value::Int(a)) => {
                (*a as f64) == *b
            }
            (Value::String(a), Value::String(b)) => a == b,
            (Value::Symbol(a), Value::Symbol(b)) => a == b,
            (Value::Array(a), Value::Array(b)) => a == b,
            (Value::Range(a1, a2, a3), Value::Range(b1, b2, b3)) => {
                a1 == b1 && a2 == b2 && a3 == b3
            }
            _ => false,
        }
    }
}

impl PartialOrd for Value {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        match (self, other) {
            (Value::Int(a), Value::Int(b)) => a.partial_cmp(b),
            (Value::Float(a), Value::Float(b)) => a.partial_cmp(b),
            (Value::Int(a), Value::Float(b)) => (*a as f64).partial_cmp(b),
            (Value::Float(a), Value::Int(b)) => a.partial_cmp(&(*b as f64)),
            (Value::String(a), Value::String(b)) => a.partial_cmp(b),
            _ => None,
        }
    }
}
