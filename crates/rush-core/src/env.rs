use std::collections::{HashMap, HashSet};

use crate::ast;
use crate::value::Value;

/// A user-defined function captured at definition time.
#[derive(Debug, Clone)]
pub struct Function {
    pub name: String,
    pub params: Vec<ast::ParamDef>,
    pub body: Vec<ast::Node>,
    /// Raw source of the function body — used for mixed Rush+shell dispatch.
    /// If present, body lines are triaged (Rush vs shell) at call time.
    pub raw_body: Option<String>,
}

/// A user-defined class.
#[derive(Debug, Clone)]
pub struct ClassDef {
    pub name: String,
    pub parent: Option<String>,
    pub attributes: Vec<ast::AttrDef>,
    pub constructor: Option<Function>,
    pub methods: HashMap<String, Function>,
    pub static_methods: HashMap<String, Function>,
}

/// Scoped variable environment with lexical parent chain.
#[derive(Debug, Clone)]
pub struct Environment {
    scopes: Vec<HashMap<String, Value>>,
    pub functions: HashMap<String, Function>,
    pub classes: HashMap<String, ClassDef>,
    readonly: HashSet<String>,
}

impl Environment {
    pub fn new() -> Self {
        Self {
            scopes: vec![HashMap::new()],
            functions: HashMap::new(),
            classes: HashMap::new(),
            readonly: HashSet::new(),
        }
    }

    /// Push a new scope (entering a function, block, etc.)
    pub fn push_scope(&mut self) {
        self.scopes.push(HashMap::new());
    }

    /// Pop the current scope.
    pub fn pop_scope(&mut self) {
        if self.scopes.len() > 1 {
            self.scopes.pop();
        }
    }

    /// Get a variable, searching from innermost scope outward.
    pub fn get(&self, name: &str) -> Option<&Value> {
        for scope in self.scopes.iter().rev() {
            if let Some(val) = scope.get(name) {
                return Some(val);
            }
        }
        None
    }

    /// Set a variable. Returns false if readonly.
    pub fn set(&mut self, name: &str, value: Value) -> bool {
        if self.readonly.contains(name) {
            eprintln!("rush: {name}: readonly variable");
            return false;
        }
        // Search existing scopes
        for scope in self.scopes.iter_mut().rev() {
            if scope.contains_key(name) {
                scope.insert(name.to_string(), value);
                return true;
            }
        }
        // Not found — define in current (innermost) scope
        self.scopes.last_mut().unwrap().insert(name.to_string(), value);
        true
    }

    /// Set in the current scope only (for function params, loop vars).
    pub fn set_local(&mut self, name: &str, value: Value) {
        self.scopes.last_mut().unwrap().insert(name.to_string(), value);
    }

    /// Mark a variable as readonly.
    pub fn mark_readonly(&mut self, name: &str) {
        self.readonly.insert(name.to_string());
    }

    /// Check if a variable is readonly.
    pub fn is_readonly(&self, name: &str) -> bool {
        self.readonly.contains(name)
    }

    /// Register a user-defined function.
    pub fn define_function(&mut self, func: Function) {
        self.functions.insert(func.name.clone(), func);
    }

    /// Register a user-defined class.
    pub fn define_class(&mut self, class: ClassDef) {
        self.classes.insert(class.name.clone(), class);
    }
}

impl Default for Environment {
    fn default() -> Self {
        Self::new()
    }
}
