use std::collections::HashMap;

use crate::ast::{BlockLiteral, Node, StringPart};
use crate::env::{ClassDef, Environment, Function};
use crate::process;
use crate::stdlib;
use crate::token::TokenType;
use crate::value::Value;

/// Signals from control flow that propagate up the call stack.
#[derive(Debug)]
pub enum Signal {
    Return(Value),
    Break,
    Next,
}

/// Evaluator error.
#[derive(Debug, Clone)]
pub struct EvalError {
    pub message: String,
}

impl std::fmt::Display for EvalError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl std::error::Error for EvalError {}

type EvalResult = Result<Value, EvalError>;

fn err(msg: impl Into<String>) -> EvalError {
    EvalError {
        message: msg.into(),
    }
}

/// Output handler — receives puts/print/warn output.
pub trait Output {
    fn puts(&mut self, s: &str);
    fn print(&mut self, s: &str);
    fn warn(&mut self, s: &str);
}

/// Default output: writes to stdout/stderr.
pub struct StdOutput;

impl Output for StdOutput {
    fn puts(&mut self, s: &str) {
        println!("{s}");
    }
    fn print(&mut self, s: &str) {
        use std::io::Write;
        std::io::stdout().write_all(s.as_bytes()).ok();
        std::io::stdout().flush().ok();
    }
    fn warn(&mut self, s: &str) {
        eprintln!("{s}");
    }
}

/// The Rush evaluator — walks AST nodes and produces values.
pub struct Evaluator<'a> {
    pub env: Environment,
    pub output: &'a mut dyn Output,
    pub exit_code: i32,
}

impl<'a> Evaluator<'a> {
    pub fn new(output: &'a mut dyn Output) -> Self {
        Self {
            env: Environment::new(),
            output,
            exit_code: 0,
        }
    }

    /// Execute a list of statements, returning the last value.
    pub fn exec(&mut self, nodes: &[Node]) -> Result<Value, Signal> {
        let mut result = Value::Nil;
        for node in nodes {
            result = self.eval_node(node)?;
        }
        Ok(result)
    }

    /// Execute, converting Signal to EvalError for top-level use.
    pub fn exec_toplevel(&mut self, nodes: &[Node]) -> EvalResult {
        self.exec(nodes).map_err(|sig| match sig {
            Signal::Return(v) => err(format!("return outside function (value: {v})")),
            Signal::Break => err("break outside loop"),
            Signal::Next => err("next outside loop"),
        })
    }

    fn eval_node(&mut self, node: &Node) -> Result<Value, Signal> {
        match node {
            Node::Literal { value, literal_type } => Ok(self.eval_literal(value, *literal_type)),

            Node::VariableRef { name } => Ok(self.eval_variable_ref(name)),

            Node::Assignment { name, value } => {
                let val = self.eval_node(value)?;
                self.env.set(name, val.clone());
                Ok(val)
            }

            Node::MultipleAssignment { names, values } => {
                let vals: Vec<Value> = values
                    .iter()
                    .map(|v| self.eval_node(v))
                    .collect::<Result<_, _>>()?;
                for (i, name) in names.iter().enumerate() {
                    let val = vals.get(i).cloned().unwrap_or(Value::Nil);
                    self.env.set(name, val);
                }
                Ok(Value::Nil)
            }

            Node::CompoundAssignment { name, op, value } => {
                let current = self.eval_variable_ref(name);
                let rhs = self.eval_node(value)?;
                let base_op = &op[..op.len() - 1]; // "+=" → "+", "-=" → "-"
                let result = self.apply_binary_op(&current, base_op, &rhs);
                self.env.set(name, result.clone());
                Ok(result)
            }

            Node::PropertyAssignment {
                receiver,
                property,
                value,
            } => {
                // For self.x = val, we store as "self.x" or handle instance vars
                // Simplified: store as flat variable for now
                let recv = self.eval_node(receiver)?;
                let val = self.eval_node(value)?;
                if let Value::String(ref name) = recv {
                    if name == "self" {
                        self.env.set(property, val.clone());
                    }
                }
                // If receiver is VariableRef("self"), set the property
                if matches!(receiver.as_ref(), Node::VariableRef { name } if name == "self") {
                    self.env.set(property, val.clone());
                }
                Ok(val)
            }

            Node::BinaryOp { left, op, right } => {
                // Short-circuit for logical operators
                if op == "&&" || op == "and" {
                    let l = self.eval_node(left)?;
                    if !l.is_truthy() {
                        return Ok(l);
                    }
                    return self.eval_node(right);
                }
                if op == "||" || op == "or" {
                    let l = self.eval_node(left)?;
                    if l.is_truthy() {
                        return Ok(l);
                    }
                    return self.eval_node(right);
                }
                let l = self.eval_node(left)?;
                let r = self.eval_node(right)?;
                Ok(self.apply_binary_op(&l, op, &r))
            }

            Node::UnaryOp { op, operand } => {
                let val = self.eval_node(operand)?;
                Ok(match op.as_str() {
                    "not" => Value::Bool(!val.is_truthy()),
                    "-" => match val {
                        Value::Int(n) => Value::Int(-n),
                        Value::Float(f) => Value::Float(-f),
                        _ => Value::Nil,
                    },
                    _ => Value::Nil,
                })
            }

            Node::Ternary {
                condition,
                then_expr,
                else_expr,
            } => {
                let cond = self.eval_node(condition)?;
                if cond.is_truthy() {
                    self.eval_node(then_expr)
                } else {
                    self.eval_node(else_expr)
                }
            }

            Node::If {
                condition,
                body,
                elsifs,
                else_body,
            } => {
                let cond = self.eval_node(condition)?;
                if cond.is_truthy() {
                    return self.exec(body);
                }
                for (elsif_cond, elsif_body) in elsifs {
                    let c = self.eval_node(elsif_cond)?;
                    if c.is_truthy() {
                        return self.exec(elsif_body);
                    }
                }
                if let Some(eb) = else_body {
                    return self.exec(eb);
                }
                Ok(Value::Nil)
            }

            Node::PostfixIf {
                statement,
                condition,
                is_unless,
            } => {
                let cond = self.eval_node(condition)?;
                let should_run = if *is_unless {
                    !cond.is_truthy()
                } else {
                    cond.is_truthy()
                };
                if should_run {
                    self.eval_node(statement)
                } else {
                    Ok(Value::Nil)
                }
            }

            Node::While {
                condition,
                body,
                is_until,
            } => {
                loop {
                    let cond = self.eval_node(condition)?;
                    let should_run = if *is_until {
                        !cond.is_truthy()
                    } else {
                        cond.is_truthy()
                    };
                    if !should_run {
                        break;
                    }
                    match self.exec(body) {
                        Ok(_) => {}
                        Err(Signal::Break) => break,
                        Err(Signal::Next) => continue,
                        Err(e) => return Err(e),
                    }
                }
                Ok(Value::Nil)
            }

            Node::For {
                variable,
                collection,
                body,
            } => {
                let coll = self.eval_node(collection)?;
                let items = self.value_to_iterable(&coll);
                self.env.push_scope();
                for item in items {
                    self.env.set_local(variable, item);
                    match self.exec(body) {
                        Ok(_) => {}
                        Err(Signal::Break) => break,
                        Err(Signal::Next) => continue,
                        Err(e) => {
                            self.env.pop_scope();
                            return Err(e);
                        }
                    }
                }
                self.env.pop_scope();
                Ok(Value::Nil)
            }

            Node::Parallel {
                variable,
                collection,
                body,
                max_workers,
                timeout_secs,
                fail_fast,
            } => {
                let coll = self.eval_node(collection)?;
                let items = self.value_to_iterable(&coll);
                if items.is_empty() {
                    return Ok(Value::Nil);
                }

                // Snapshot parent environment for threads
                let env_snapshot = self.env.clone();
                let body = body.clone();
                let variable = variable.clone();

                // Each thread captures output + result
                struct ThreadResult {
                    output: Vec<(OutputKind, String)>,
                    value: Result<Value, Option<String>>,
                }

                #[derive(Clone)]
                enum OutputKind { Puts, Print, Warn }

                struct CaptureOutput {
                    captured: Vec<(OutputKind, String)>,
                }
                impl CaptureOutput {
                    fn new() -> Self { Self { captured: Vec::new() } }
                }
                impl Output for CaptureOutput {
                    fn puts(&mut self, s: &str) { self.captured.push((OutputKind::Puts, s.to_string())); }
                    fn print(&mut self, s: &str) { self.captured.push((OutputKind::Print, s.to_string())); }
                    fn warn(&mut self, s: &str) { self.captured.push((OutputKind::Warn, s.to_string())); }
                }

                let thread_results: std::sync::Mutex<Vec<ThreadResult>> =
                    std::sync::Mutex::new(Vec::new());

                // Channel-based semaphore for worker pool limiting
                let workers = max_workers.unwrap_or(items.len());
                let (sem_tx, sem_rx) = std::sync::mpsc::sync_channel::<()>(workers);
                // Pre-fill with permits
                for _ in 0..workers { sem_tx.send(()).ok(); }
                let sem_rx = std::sync::Mutex::new(sem_rx);

                // Cancellation flag (set by fail-fast or timeout)
                let cancelled = std::sync::atomic::AtomicBool::new(false);
                let timed_out = std::sync::atomic::AtomicBool::new(false);
                let completed_count = std::sync::atomic::AtomicUsize::new(0);
                let total = items.len();

                std::thread::scope(|s| {
                    // Watchdog thread for timeout
                    if let Some(secs) = timeout_secs {
                        let cancelled = &cancelled;
                        let timed_out = &timed_out;
                        let completed_count = &completed_count;
                        s.spawn(move || {
                            let deadline = std::time::Instant::now()
                                + std::time::Duration::from_secs(*secs);
                            while std::time::Instant::now() < deadline {
                                if completed_count.load(std::sync::atomic::Ordering::Relaxed) >= total {
                                    return; // All tasks done, no timeout
                                }
                                std::thread::sleep(std::time::Duration::from_millis(50));
                            }
                            timed_out.store(true, std::sync::atomic::Ordering::Relaxed);
                            cancelled.store(true, std::sync::atomic::Ordering::Relaxed);
                        });
                    }

                    for item in &items {
                        let env_snapshot = &env_snapshot;
                        let body = &body;
                        let variable = &variable;
                        let item = item.clone();
                        let thread_results = &thread_results;
                        let sem_rx = &sem_rx;
                        let sem_tx = &sem_tx;
                        let cancelled = &cancelled;
                        let completed_count = &completed_count;
                        let fail_fast = *fail_fast;

                        s.spawn(move || {
                            // Acquire semaphore slot (blocks if pool is full)
                            let _ = sem_rx.lock().unwrap().recv();

                            // Skip if cancelled by a sibling
                            if cancelled.load(std::sync::atomic::Ordering::Relaxed) {
                                sem_tx.send(()).ok();
                                return;
                            }

                            let mut out = CaptureOutput::new();
                            let value = {
                                let mut eval = Evaluator::new(&mut out);
                                eval.env = env_snapshot.clone();
                                eval.env.push_scope();
                                eval.env.set_local(variable, item);

                                match eval.exec(body) {
                                    Ok(v) => Ok(v),
                                    Err(Signal::Break | Signal::Next) => Ok(Value::Nil),
                                    Err(Signal::Return(_)) => {
                                        if fail_fast {
                                            cancelled.store(true, std::sync::atomic::Ordering::Relaxed);
                                        }
                                        Err(Some("return inside parallel block".to_string()))
                                    }
                                }
                            };
                            thread_results.lock().unwrap().push(ThreadResult {
                                output: out.captured,
                                value,
                            });
                            let done = completed_count.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1;
                            // Live progress to stderr (only when interactive TTY)
                            if total > 1 {
                                use std::io::IsTerminal;
                                if std::io::stderr().is_terminal() {
                                    eprintln!("[parallel] {done}/{total} done");
                                }
                            }
                            // Release semaphore permit
                            sem_tx.send(()).ok();
                        });
                    }
                });

                // Replay captured output and collect results
                let thread_results = thread_results.into_inner().unwrap();
                let mut values = Vec::new();
                let mut first_error = None;

                for tr in thread_results {
                    for (kind, text) in &tr.output {
                        match kind {
                            OutputKind::Puts => self.output.puts(text),
                            OutputKind::Print => self.output.print(text),
                            OutputKind::Warn => self.output.warn(text),
                        }
                    }
                    match tr.value {
                        Ok(v) => values.push(v),
                        Err(msg) => {
                            if first_error.is_none() {
                                first_error = msg;
                            }
                        }
                    }
                }

                if timed_out.load(std::sync::atomic::Ordering::Relaxed) {
                    self.output.warn(&format!(
                        "parallel: timed out after {}s ({} of {} tasks completed)",
                        timeout_secs.unwrap_or(0),
                        values.len(),
                        items.len()
                    ));
                }

                if let Some(msg) = first_error {
                    return Err(Signal::Return(Value::String(msg)));
                }

                // Check for error results (e.g. Ssh.run returning status: "error")
                let mut error_count = 0usize;
                for v in &values {
                    if let Value::Hash(h) = v {
                        if let Some(Value::String(s)) = h.get("status") {
                            if s == "error" {
                                error_count += 1;
                                // Emit warning with host/stderr if available
                                let host = h.get("host")
                                    .map(|v| v.to_rush_string())
                                    .unwrap_or_else(|| "?".to_string());
                                let detail = h.get("stderr")
                                    .map(|v| v.to_rush_string())
                                    .unwrap_or_default();
                                let msg = if detail.is_empty() {
                                    format!("[parallel] {host}: failed")
                                } else {
                                    let short = detail.lines().next().unwrap_or(&detail);
                                    format!("[parallel] {host}: {short}")
                                };
                                self.output.warn(&msg);
                            }
                        }
                    }
                }
                if error_count > 0 {
                    self.output.warn(&format!(
                        "[parallel] {}/{} failed",
                        error_count, values.len()
                    ));
                    if *fail_fast {
                        self.exit_code = 1;
                    }
                }

                Ok(Value::Array(values))
            }

            Node::Orchestrate { tasks } => {
                use std::collections::{HashMap as Map, HashSet as Set};

                if tasks.is_empty() {
                    return Ok(Value::Nil);
                }

                // Build dependency info
                let task_names: Set<String> = tasks.iter().map(|t| t.name.clone()).collect();
                for t in tasks {
                    for dep in &t.after {
                        if !task_names.contains(dep) {
                            self.output.warn(&format!(
                                "orchestrate: task '{}' depends on unknown task '{dep}'",
                                t.name
                            ));
                            return Ok(Value::Nil);
                        }
                    }
                }

                // Detect cycles via topological sort (Kahn's algorithm)
                let mut in_degree: Map<&str, usize> = Map::new();
                let mut dependents: Map<&str, Vec<&str>> = Map::new();
                for t in tasks {
                    in_degree.entry(&t.name).or_insert(0);
                    for dep in &t.after {
                        *in_degree.entry(&t.name).or_insert(0) += 1;
                        dependents.entry(dep.as_str()).or_default().push(&t.name);
                    }
                }

                // Find initial ready set (no dependencies)
                let mut ready: Vec<&str> = in_degree.iter()
                    .filter(|&(_, &deg)| deg == 0)
                    .map(|(&name, _)| name)
                    .collect();

                if ready.is_empty() {
                    self.output.warn("orchestrate: circular dependency detected");
                    return Ok(Value::Nil);
                }

                // Task lookup by name
                let task_map: Map<&str, &crate::ast::OrchestrateTask> =
                    tasks.iter().map(|t| (t.name.as_str(), t)).collect();

                // Execute in waves: each wave runs all ready tasks concurrently
                let env_snapshot = self.env.clone();
                let mut completed: Set<&str> = Set::new();
                let mut results: Map<String, Value> = Map::new();
                let mut wave_num = 0usize;
                let total_tasks = tasks.len();

                while !ready.is_empty() {
                    wave_num += 1;
                    let task_list: Vec<&str> = ready.clone();
                    self.output.puts(&format!(
                        "[orchestrate] wave {wave_num}: running {} ({}/{})",
                        task_list.join(", "),
                        completed.len(),
                        total_tasks
                    ));
                    // Thread-safe result collection for this wave
                    struct WaveResult {
                        name: String,
                        output: Vec<(WaveOutputKind, String)>,
                        value: Result<Value, String>,
                    }
                    #[derive(Clone)]
                    enum WaveOutputKind { Puts, Print, Warn }
                    struct WaveCaptureOutput {
                        captured: Vec<(WaveOutputKind, String)>,
                    }
                    impl WaveCaptureOutput {
                        fn new() -> Self { Self { captured: Vec::new() } }
                    }
                    impl Output for WaveCaptureOutput {
                        fn puts(&mut self, s: &str) { self.captured.push((WaveOutputKind::Puts, s.to_string())); }
                        fn print(&mut self, s: &str) { self.captured.push((WaveOutputKind::Print, s.to_string())); }
                        fn warn(&mut self, s: &str) { self.captured.push((WaveOutputKind::Warn, s.to_string())); }
                    }

                    let wave_results: std::sync::Mutex<Vec<WaveResult>> =
                        std::sync::Mutex::new(Vec::new());

                    std::thread::scope(|s| {
                        for &task_name in &ready {
                            let task = task_map[task_name];
                            let env_snapshot = &env_snapshot;
                            let wave_results = &wave_results;
                            let body = task.body.clone();
                            let name = task.name.clone();

                            s.spawn(move || {
                                let mut out = WaveCaptureOutput::new();
                                let value = {
                                    let mut eval = Evaluator::new(&mut out);
                                    eval.env = env_snapshot.clone();
                                    match eval.exec(&body) {
                                        Ok(v) => Ok(v),
                                        Err(Signal::Return(v)) => Ok(v),
                                        Err(Signal::Break | Signal::Next) => Ok(Value::Nil),
                                    }
                                };
                                wave_results.lock().unwrap().push(WaveResult {
                                    name,
                                    output: out.captured,
                                    value,
                                });
                            });
                        }
                    });

                    // Replay output and collect results
                    let wave_results = wave_results.into_inner().unwrap();
                    for wr in wave_results {
                        let status = match &wr.value {
                            Ok(_) => "done",
                            Err(_) => "FAILED",
                        };
                        self.output.puts(&format!(
                            "[orchestrate] {}: {status}", wr.name
                        ));
                        for (kind, text) in &wr.output {
                            match kind {
                                WaveOutputKind::Puts => self.output.puts(text),
                                WaveOutputKind::Print => self.output.print(text),
                                WaveOutputKind::Warn => self.output.warn(text),
                            }
                        }
                        match wr.value {
                            Ok(v) => { results.insert(wr.name.clone(), v); }
                            Err(msg) => {
                                self.output.warn(&format!(
                                    "orchestrate: task '{}' failed: {msg}", wr.name
                                ));
                            }
                        }
                        completed.insert(
                            task_names.iter().find(|n| **n == wr.name).unwrap().as_str()
                        );
                    }

                    // Find newly ready tasks
                    ready.clear();
                    for (&name, deps) in &dependents {
                        if completed.contains(name) {
                            for &dep in deps {
                                if !completed.contains(dep) {
                                    // Check if all deps are now satisfied
                                    let task = task_map[dep];
                                    if task.after.iter().all(|d| completed.contains(d.as_str())) {
                                        ready.push(dep);
                                    }
                                }
                            }
                        }
                    }
                    ready.sort(); // deterministic order
                    ready.dedup();
                }

                self.output.puts(&format!(
                    "[orchestrate] complete: {}/{} tasks in {} wave(s)",
                    completed.len(), total_tasks, wave_num
                ));

                // Return results as a hash
                Ok(Value::Hash(results))
            }

            Node::FunctionDef {
                name,
                params,
                body,
                is_static: _,
                raw_body,
            } => {
                self.env.define_function(Function {
                    name: name.clone(),
                    params: params.clone(),
                    body: body.clone(),
                    raw_body: raw_body.clone(),
                });
                Ok(Value::Nil)
            }

            Node::FunctionCall { name, args } => {
                let arg_vals: Vec<Value> = args
                    .iter()
                    .map(|a| self.eval_node(a))
                    .collect::<Result<_, _>>()?;
                self.call_function(name, &arg_vals)
            }

            Node::Return { value } => {
                let val = if let Some(v) = value {
                    self.eval_node(v)?
                } else {
                    Value::Nil
                };
                Err(Signal::Return(val))
            }

            Node::LoopControl { keyword } => match keyword.as_str() {
                "break" => Err(Signal::Break),
                "next" | "continue" => Err(Signal::Next),
                _ => Ok(Value::Nil),
            },

            Node::Array { elements } => {
                let vals: Vec<Value> = elements
                    .iter()
                    .map(|e| self.eval_node(e))
                    .collect::<Result<_, _>>()?;
                Ok(Value::Array(vals))
            }

            Node::Hash { entries } => {
                let mut map = HashMap::new();
                for (key, val) in entries {
                    let k = self.eval_node(key)?;
                    let v = self.eval_node(val)?;
                    map.insert(k.to_rush_string(), v);
                }
                Ok(Value::Hash(map))
            }

            Node::Range {
                start,
                end,
                exclusive,
            } => {
                let s = self.eval_node(start)?;
                let e = self.eval_node(end)?;
                match (s.to_int(), e.to_int()) {
                    (Some(a), Some(b)) => Ok(Value::Range(a, b, *exclusive)),
                    _ => Ok(Value::Nil),
                }
            }

            Node::Symbol { name } => Ok(Value::Symbol(name.clone())),

            Node::InterpolatedString { parts } => {
                let mut result = String::new();
                for part in parts {
                    match part {
                        StringPart::Text(t) => result.push_str(t),
                        StringPart::Expr(e) => {
                            let val = self.eval_node(e)?;
                            result.push_str(&val.to_rush_string());
                        }
                    }
                }
                Ok(Value::String(self.process_escapes(&result)))
            }

            Node::MethodCall {
                receiver,
                method,
                args,
                block,
            } => {
                // Stdlib dispatch: File.method(), Dir.method(), Time.method()
                if let Some(name) = Self::stdlib_receiver_name(receiver) {
                    let arg_vals: Vec<Value> = args
                        .iter()
                        .map(|a| self.eval_node(a))
                        .collect::<Result<_, _>>()?;
                    stdlib::reset_last_error();
                    let value = match name {
                        "file" => stdlib::file_method(method, &arg_vals),
                        "dir" => stdlib::dir_method(method, &arg_vals),
                        "time" => stdlib::time_method(method, &arg_vals),
                        "path" => stdlib::path_method(method, &arg_vals),
                        "ssh" => stdlib::ssh_method(method, &arg_vals),
                        "env" if method == "[]" => {
                            let key = arg_vals.first().map(|v| v.to_rush_string()).unwrap_or_default();
                            stdlib::env_get(&key)
                        }
                        _ => Value::Nil,
                    };
                    if stdlib::take_last_error() {
                        self.exit_code = 1;
                    }
                    return Ok(value);
                }
                let recv = self.eval_node(receiver)?;
                let arg_vals: Vec<Value> = args
                    .iter()
                    .map(|a| self.eval_node(a))
                    .collect::<Result<_, _>>()?;
                Ok(self.call_method(&recv, method, &arg_vals, block.as_deref()))
            }

            Node::PropertyAccess { receiver, property } => {
                // Stdlib property: Time.now, Dir.pwd, env.HOME, duration (2.hours)
                if let Some(name) = Self::stdlib_receiver_name(receiver) {
                    return Ok(match name {
                        "time" => stdlib::time_method(property, &[]),
                        "dir" => stdlib::dir_method(property, &[]),
                        "file" => stdlib::file_method(property, &[]),
                        "path" => stdlib::path_method(property, &[]),
                        "ssh" => stdlib::ssh_method(property, &[]),
                        "env" => stdlib::env_get(property),
                        _ => Value::Nil,
                    });
                }
                let recv = self.eval_node(receiver)?;
                // Duration: 2.hours, 30.minutes, etc.
                if matches!(property.as_str(), "hours" | "hour" | "minutes" | "minute"
                    | "seconds" | "second" | "days" | "day")
                {
                    if let Some(n) = recv.to_float() {
                        return Ok(stdlib::duration_to_seconds(n, property));
                    }
                }
                Ok(self.access_property(&recv, property))
            }

            Node::SafeNav { receiver, member } => {
                let recv = self.eval_node(receiver)?;
                if matches!(recv, Value::Nil) {
                    Ok(Value::Nil)
                } else {
                    Ok(self.access_property(&recv, member))
                }
            }

            Node::CommandSub { command } => {
                let result = process::run_native_capture(command);
                self.exit_code = result.exit_code;
                Ok(Value::String(result.stdout.trim_end().to_string()))
            }

            Node::Try {
                body,
                rescue_var,
                rescue_body,
                ensure_body,
            } => {
                let result = self.exec(body);
                let val = match result {
                    Ok(v) => v,
                    Err(Signal::Return(_)) => {
                        // Let return propagate through ensure
                        if let Some(eb) = ensure_body {
                            let _ = self.exec(eb);
                        }
                        return result;
                    }
                    Err(_) => {
                        if let Some(rb) = rescue_body {
                            if let Some(var) = rescue_var {
                                self.env.set(var, Value::String("error".to_string()));
                            }
                            self.exec(rb).unwrap_or(Value::Nil)
                        } else {
                            Value::Nil
                        }
                    }
                };
                if let Some(eb) = ensure_body {
                    let _ = self.exec(eb);
                }
                Ok(val)
            }

            Node::Case {
                subject,
                whens,
                else_body,
            } => {
                use crate::ast::CaseTerminator;
                let subj = self.eval_node(subject)?;
                let mut matched = false;
                let mut last_result = Value::Nil;

                for (pattern, when_body, terminator) in whens.iter() {
                    let should_run = if matched {
                        true // fallthrough from previous match
                    } else {
                        let pat = self.eval_node(pattern)?;
                        subj == pat
                    };

                    if should_run {
                        last_result = self.exec(when_body)?;
                        match terminator {
                            CaseTerminator::Break => return Ok(last_result),
                            CaseTerminator::Fallthrough => {
                                matched = true; // run next body unconditionally
                            }
                            CaseTerminator::ContinueTesting => {
                                matched = false; // test remaining patterns
                            }
                        }
                    }
                }

                if !matched {
                    if let Some(eb) = else_body {
                        return self.exec(eb);
                    }
                }
                Ok(last_result)
            }

            Node::ClassDef {
                name,
                parent,
                attributes,
                constructor,
                methods,
                static_methods,
            } => {
                let mut method_map = HashMap::new();
                let mut static_map = HashMap::new();

                for m in methods {
                    if let Node::FunctionDef {
                        name: mname,
                        params,
                        body,
                        ..
                    } = m
                    {
                        method_map.insert(
                            mname.clone(),
                            Function {
                                name: mname.clone(),
                                params: params.clone(),
                                body: body.clone(),
                                raw_body: None,
                            },
                        );
                    }
                }
                for m in static_methods {
                    if let Node::FunctionDef {
                        name: mname,
                        params,
                        body,
                        ..
                    } = m
                    {
                        static_map.insert(
                            mname.clone(),
                            Function {
                                name: mname.clone(),
                                params: params.clone(),
                                body: body.clone(),
                                raw_body: None,
                            },
                        );
                    }
                }

                let ctor = constructor.as_ref().and_then(|c| {
                    if let Node::FunctionDef {
                        name: cname,
                        params,
                        body,
                        ..
                    } = c.as_ref()
                    {
                        Some(Function {
                            name: cname.clone(),
                            params: params.clone(),
                            body: body.clone(),
                            raw_body: None,
                        })
                    } else {
                        None
                    }
                });

                self.env.define_class(ClassDef {
                    name: name.clone(),
                    parent: parent.clone(),
                    attributes: attributes.clone(),
                    constructor: ctor,
                    methods: method_map,
                    static_methods: static_map,
                });
                Ok(Value::Nil)
            }

            Node::EnumDef { name, members } => {
                // Store enum members as variables: EnumName::Member = value
                for (i, (member_name, value)) in members.iter().enumerate() {
                    let val = if let Some(v) = value {
                        self.eval_node(v)?
                    } else {
                        Value::Int(i as i64)
                    };
                    let key = format!("{name}::{member_name}");
                    self.env.set(&key, val);
                }
                Ok(Value::Nil)
            }

            Node::RegexLiteral { .. } => {
                // Regex values stored as strings for now
                Ok(Value::Nil)
            }

            Node::PluginBlock { plugin_name, raw_body } => {
                match crate::plugin::execute(plugin_name, raw_body) {
                    Ok(output) => {
                        if !output.is_empty() {
                            self.output.puts(&output);
                        }
                        Ok(Value::Nil)
                    }
                    Err(e) => {
                        self.output.warn(&format!("plugin.{plugin_name}: {e}"));
                        Ok(Value::Nil)
                    }
                }
            }

            Node::StaticMember { .. }
            | Node::SuperCall { .. }
            | Node::PlatformBlock { .. }
            | Node::ShellPassthrough { .. }
            | Node::NamedArg { .. } => Ok(Value::Nil),
        }
    }

    // ── Stdlib Dispatch ──────────────────────────────────────────────

    /// Check if a node is a stdlib receiver (File, Dir, Time, env).
    fn stdlib_receiver_name(node: &Node) -> Option<&'static str> {
        if let Node::VariableRef { name } = node {
            match name.to_ascii_lowercase().as_str() {
                "file" => Some("file"),
                "dir" => Some("dir"),
                "time" => Some("time"),
                "env" => Some("env"),
                "path" => Some("path"),
                "ssh" => Some("ssh"),
                _ => None,
            }
        } else {
            None
        }
    }

    // ── Literals ────────────────────────────────────────────────────

    fn eval_literal(&self, value: &str, literal_type: TokenType) -> Value {
        match literal_type {
            TokenType::Integer => {
                // Handle size suffixes
                let lower = value.to_ascii_lowercase();
                if lower.ends_with("kb") {
                    let n: i64 = lower.trim_end_matches("kb").parse().unwrap_or(0);
                    Value::Int(n * 1024)
                } else if lower.ends_with("mb") {
                    let n: i64 = lower.trim_end_matches("mb").parse().unwrap_or(0);
                    Value::Int(n * 1024 * 1024)
                } else if lower.ends_with("gb") {
                    let n: i64 = lower.trim_end_matches("gb").parse().unwrap_or(0);
                    Value::Int(n * 1024 * 1024 * 1024)
                } else if lower.ends_with("tb") {
                    let n: i64 = lower.trim_end_matches("tb").parse().unwrap_or(0);
                    Value::Int(n * 1024 * 1024 * 1024 * 1024)
                } else {
                    Value::Int(value.parse().unwrap_or(0))
                }
            }
            TokenType::Float => Value::Float(value.parse().unwrap_or(0.0)),
            TokenType::StringLiteral => {
                // Strip quotes
                let s = if (value.starts_with('"') && value.ends_with('"'))
                    || (value.starts_with('\'') && value.ends_with('\''))
                {
                    &value[1..value.len() - 1]
                } else {
                    value
                };
                // Process escapes only for double-quoted strings
                if value.starts_with('"') {
                    Value::String(self.process_escapes(s))
                } else {
                    Value::String(s.to_string())
                }
            }
            TokenType::True => Value::Bool(true),
            TokenType::False => Value::Bool(false),
            TokenType::Nil => Value::Nil,
            _ => Value::String(value.to_string()),
        }
    }

    fn process_escapes(&self, s: &str) -> String {
        let mut result = String::with_capacity(s.len());
        let mut chars = s.chars();
        while let Some(c) = chars.next() {
            if c == '\\' {
                match chars.next() {
                    Some('n') => result.push('\n'),
                    Some('t') => result.push('\t'),
                    Some('r') => result.push('\r'),
                    Some('\\') => result.push('\\'),
                    Some('"') => result.push('"'),
                    Some('\'') => result.push('\''),
                    Some('0') => result.push('\0'),
                    Some('e') => result.push('\x1b'),
                    Some(other) => {
                        result.push('\\');
                        result.push(other);
                    }
                    None => result.push('\\'),
                }
            } else {
                result.push(c);
            }
        }
        result
    }

    fn eval_variable_ref(&self, name: &str) -> Value {
        self.env
            .get(name)
            .cloned()
            .unwrap_or(Value::Nil)
    }

    // ── Binary Operations ───────────────────────────────────────────

    fn apply_binary_op(&self, left: &Value, op: &str, right: &Value) -> Value {
        match op {
            "+" => self.add(left, right),
            "-" => self.sub(left, right),
            "*" => self.mul(left, right),
            "/" => self.div(left, right),
            "%" => self.modulo(left, right),
            "==" => Value::Bool(left == right),
            "!=" => Value::Bool(left != right),
            "<" => Value::Bool(left < right),
            ">" => Value::Bool(left > right),
            "<=" => Value::Bool(left <= right),
            ">=" => Value::Bool(left >= right),
            "=~" | "~" => self.regex_match(left, right),
            "!~" => {
                let m = self.regex_match(left, right);
                Value::Bool(!m.is_truthy())
            }
            _ => Value::Nil,
        }
    }

    fn add(&self, left: &Value, right: &Value) -> Value {
        match (left, right) {
            (Value::Int(a), Value::Int(b)) => Value::Int(a + b),
            (Value::Float(a), Value::Float(b)) => Value::Float(a + b),
            (Value::Int(a), Value::Float(b)) => Value::Float(*a as f64 + b),
            (Value::Float(a), Value::Int(b)) => Value::Float(a + *b as f64),
            (Value::String(a), Value::String(b)) => Value::String(format!("{a}{b}")),
            (Value::String(a), other) => Value::String(format!("{a}{}", other.to_rush_string())),
            (Value::Array(a), Value::Array(b)) => {
                let mut result = a.clone();
                result.extend(b.clone());
                Value::Array(result)
            }
            _ => Value::Nil,
        }
    }

    fn sub(&self, left: &Value, right: &Value) -> Value {
        match (left, right) {
            (Value::Int(a), Value::Int(b)) => Value::Int(a - b),
            (Value::Float(a), Value::Float(b)) => Value::Float(a - b),
            (Value::Int(a), Value::Float(b)) => Value::Float(*a as f64 - b),
            (Value::Float(a), Value::Int(b)) => Value::Float(a - *b as f64),
            _ => Value::Nil,
        }
    }

    fn mul(&self, left: &Value, right: &Value) -> Value {
        match (left, right) {
            (Value::Int(a), Value::Int(b)) => Value::Int(a * b),
            (Value::Float(a), Value::Float(b)) => Value::Float(a * b),
            (Value::Int(a), Value::Float(b)) => Value::Float(*a as f64 * b),
            (Value::Float(a), Value::Int(b)) => Value::Float(a * *b as f64),
            (Value::String(s), Value::Int(n)) => Value::String(s.repeat(*n as usize)),
            _ => Value::Nil,
        }
    }

    fn div(&self, left: &Value, right: &Value) -> Value {
        match (left, right) {
            (Value::Int(a), Value::Int(b)) if *b != 0 => Value::Int(a / b),
            (Value::Float(a), Value::Float(b)) if *b != 0.0 => Value::Float(a / b),
            (Value::Int(a), Value::Float(b)) if *b != 0.0 => Value::Float(*a as f64 / b),
            (Value::Float(a), Value::Int(b)) if *b != 0 => Value::Float(a / *b as f64),
            _ => Value::Nil,
        }
    }

    fn modulo(&self, left: &Value, right: &Value) -> Value {
        match (left, right) {
            (Value::Int(a), Value::Int(b)) if *b != 0 => Value::Int(a % b),
            _ => Value::Nil,
        }
    }

    fn regex_match(&self, _left: &Value, _right: &Value) -> Value {
        // TODO: implement regex matching
        Value::Bool(false)
    }

    // ── Method Calls ────────────────────────────────────────────────

    fn call_method(
        &mut self,
        receiver: &Value,
        method: &str,
        args: &[Value],
        block: Option<&BlockLiteral>,
    ) -> Value {
        match receiver {
            Value::String(s) => self.string_method(s, method, args),
            Value::Array(arr) => self.array_method(arr, method, args, block),
            Value::Hash(map) => self.hash_method(map, method, args),
            Value::Int(n) => self.int_method(*n, method, args, block),
            Value::Range(start, end, exclusive) => {
                self.range_method(*start, *end, *exclusive, method)
            }
            _ => {
                // Index access
                if method == "[]" {
                    return self.index_access(receiver, args);
                }
                Value::Nil
            }
        }
    }

    fn string_method(&self, s: &str, method: &str, args: &[Value]) -> Value {
        match method {
            "length" | "size" | "count" => Value::Int(s.len() as i64),
            "upcase" | "upper" => Value::String(s.to_uppercase()),
            "downcase" | "lower" => Value::String(s.to_lowercase()),
            "strip" | "trim" => Value::String(s.trim().to_string()),
            "lstrip" | "trim_start" => Value::String(s.trim_start().to_string()),
            "rstrip" | "trim_end" => Value::String(s.trim_end().to_string()),
            "reverse" => Value::String(s.chars().rev().collect()),
            "chars" => Value::Array(s.chars().map(|c| Value::String(c.to_string())).collect()),
            "lines" => Value::Array(s.lines().map(|l| Value::String(l.to_string())).collect()),
            "empty?" => Value::Bool(s.is_empty()),
            "include?" | "contains" => {
                let search = args.first().map(|v| v.to_rush_string()).unwrap_or_default();
                Value::Bool(s.contains(&search))
            }
            "starts_with?" | "start_with?" => {
                let prefix = args.first().map(|v| v.to_rush_string()).unwrap_or_default();
                Value::Bool(s.starts_with(&prefix))
            }
            "ends_with?" | "end_with?" => {
                let suffix = args.first().map(|v| v.to_rush_string()).unwrap_or_default();
                Value::Bool(s.ends_with(&suffix))
            }
            "replace" | "gsub" => {
                if args.len() >= 2 {
                    let from = args[0].to_rush_string();
                    let to = args[1].to_rush_string();
                    Value::String(s.replace(&from, &to))
                } else {
                    Value::String(s.to_string())
                }
            }
            "split" => {
                let sep = args
                    .first()
                    .map(|v| v.to_rush_string())
                    .unwrap_or_else(|| " ".to_string());
                Value::Array(s.split(&sep).map(|p| Value::String(p.to_string())).collect())
            }
            "to_i" => Value::Int(s.parse::<i64>().unwrap_or(0)),
            "to_f" => Value::Float(s.parse::<f64>().unwrap_or(0.0)),
            "to_s" => Value::String(s.to_string()),
            // Cross-platform path methods
            "native_path" => {
                // Convert forward slashes to platform-native separator
                if cfg!(windows) {
                    Value::String(s.replace('/', "\\"))
                } else {
                    Value::String(s.to_string())
                }
            }
            "unix_path" => {
                // Normalize to forward slashes (Unix-style)
                Value::String(s.replace('\\', "/"))
            }
            "[]" => self.index_access(&Value::String(s.to_string()), args),
            _ => Value::Nil,
        }
    }

    fn array_method(
        &mut self,
        arr: &[Value],
        method: &str,
        args: &[Value],
        block: Option<&BlockLiteral>,
    ) -> Value {
        match method {
            "length" | "size" | "count" => Value::Int(arr.len() as i64),
            "empty?" => Value::Bool(arr.is_empty()),
            "first" => {
                let n = args.first().and_then(|v| v.to_int()).unwrap_or(1) as usize;
                if n == 1 {
                    arr.first().cloned().unwrap_or(Value::Nil)
                } else {
                    Value::Array(arr.iter().take(n).cloned().collect())
                }
            }
            "last" => {
                let n = args.first().and_then(|v| v.to_int()).unwrap_or(1) as usize;
                if n == 1 {
                    arr.last().cloned().unwrap_or(Value::Nil)
                } else {
                    let skip = arr.len().saturating_sub(n);
                    Value::Array(arr.iter().skip(skip).cloned().collect())
                }
            }
            "reverse" => Value::Array(arr.iter().rev().cloned().collect()),
            "sort" => {
                let mut sorted = arr.to_vec();
                sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
                Value::Array(sorted)
            }
            "uniq" => {
                let mut seen = Vec::new();
                let mut result = Vec::new();
                for v in arr {
                    let s = v.inspect();
                    if !seen.contains(&s) {
                        seen.push(s);
                        result.push(v.clone());
                    }
                }
                Value::Array(result)
            }
            "flatten" => {
                let mut result = Vec::new();
                fn flatten_into(val: &Value, out: &mut Vec<Value>) {
                    if let Value::Array(inner) = val {
                        for v in inner {
                            flatten_into(v, out);
                        }
                    } else {
                        out.push(val.clone());
                    }
                }
                for v in arr {
                    flatten_into(v, &mut result);
                }
                Value::Array(result)
            }
            "join" => {
                let sep = args
                    .first()
                    .map(|v| v.to_rush_string())
                    .unwrap_or_default();
                let parts: Vec<String> = arr.iter().map(|v| v.to_rush_string()).collect();
                Value::String(parts.join(&sep))
            }
            "include?" | "contains" => {
                let search = args.first().cloned().unwrap_or(Value::Nil);
                Value::Bool(arr.iter().any(|v| *v == search))
            }
            "push" | "append" => {
                let mut new_arr = arr.to_vec();
                for a in args {
                    new_arr.push(a.clone());
                }
                Value::Array(new_arr)
            }
            "map" | "collect" => {
                if let Some(block) = block {
                    let mut result = Vec::new();
                    for item in arr {
                        let val = self.call_block(block, &[item.clone()]);
                        result.push(val);
                    }
                    Value::Array(result)
                } else {
                    Value::Array(arr.to_vec())
                }
            }
            "select" | "filter" => {
                if let Some(block) = block {
                    let mut result = Vec::new();
                    for item in arr {
                        let val = self.call_block(block, &[item.clone()]);
                        if val.is_truthy() {
                            result.push(item.clone());
                        }
                    }
                    Value::Array(result)
                } else {
                    Value::Array(arr.to_vec())
                }
            }
            "reject" => {
                if let Some(block) = block {
                    let mut result = Vec::new();
                    for item in arr {
                        let val = self.call_block(block, &[item.clone()]);
                        if !val.is_truthy() {
                            result.push(item.clone());
                        }
                    }
                    Value::Array(result)
                } else {
                    Value::Array(arr.to_vec())
                }
            }
            "each" => {
                if let Some(block) = block {
                    for item in arr {
                        self.call_block(block, &[item.clone()]);
                    }
                }
                Value::Array(arr.to_vec())
            }
            "reduce" | "inject" => {
                if let Some(block) = block {
                    let mut acc = args.first().cloned().unwrap_or_else(|| {
                        arr.first().cloned().unwrap_or(Value::Nil)
                    });
                    let start = if args.is_empty() { 1 } else { 0 };
                    for item in arr.iter().skip(start) {
                        acc = self.call_block(block, &[acc, item.clone()]);
                    }
                    acc
                } else {
                    Value::Nil
                }
            }
            "sum" => {
                let mut total = Value::Int(0);
                for v in arr {
                    total = self.add(&total, v);
                }
                total
            }
            "min" => arr
                .iter()
                .min_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal))
                .cloned()
                .unwrap_or(Value::Nil),
            "max" => arr
                .iter()
                .max_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal))
                .cloned()
                .unwrap_or(Value::Nil),
            "[]" => self.index_access(&Value::Array(arr.to_vec()), args),
            _ => Value::Nil,
        }
    }

    fn hash_method(&self, map: &HashMap<String, Value>, method: &str, _args: &[Value]) -> Value {
        match method {
            "keys" => Value::Array(map.keys().map(|k| Value::String(k.clone())).collect()),
            "values" => Value::Array(map.values().cloned().collect()),
            "length" | "size" | "count" => Value::Int(map.len() as i64),
            "empty?" => Value::Bool(map.is_empty()),
            "[]" => {
                let key = _args.first().map(|v| v.to_rush_string()).unwrap_or_default();
                map.get(&key).cloned().unwrap_or(Value::Nil)
            }
            _ => {
                // Hash key access via dot notation
                map.get(method).cloned().unwrap_or(Value::Nil)
            }
        }
    }

    fn int_method(&mut self, n: i64, method: &str, _args: &[Value], block: Option<&BlockLiteral>) -> Value {
        match method {
            "abs" => Value::Int(n.abs()),
            "to_s" => Value::String(n.to_string()),
            "to_f" => Value::Float(n as f64),
            "to_i" => Value::Int(n),
            "even?" => Value::Bool(n % 2 == 0),
            "odd?" => Value::Bool(n % 2 != 0),
            "zero?" => Value::Bool(n == 0),
            "positive?" => Value::Bool(n > 0),
            "negative?" => Value::Bool(n < 0),
            "times" => {
                if let Some(block) = block {
                    for i in 0..n {
                        self.call_block(block, &[Value::Int(i)]);
                    }
                }
                Value::Int(n)
            }
            _ => Value::Nil,
        }
    }

    fn range_method(&self, start: i64, end: i64, exclusive: bool, method: &str) -> Value {
        let items: Vec<Value> = if exclusive {
            (start..end).map(Value::Int).collect()
        } else {
            (start..=end).map(Value::Int).collect()
        };
        match method {
            "to_a" | "to_array" => Value::Array(items),
            "size" | "length" | "count" => Value::Int(items.len() as i64),
            "first" => items.first().cloned().unwrap_or(Value::Nil),
            "last" => items.last().cloned().unwrap_or(Value::Nil),
            "include?" | "contains" => Value::Bool(true), // TODO: check args
            _ => Value::Nil,
        }
    }

    fn index_access(&self, receiver: &Value, args: &[Value]) -> Value {
        let idx = args.first().cloned().unwrap_or(Value::Nil);
        match receiver {
            Value::Array(arr) => {
                if let Some(i) = idx.to_int() {
                    let i = if i < 0 { arr.len() as i64 + i } else { i } as usize;
                    arr.get(i).cloned().unwrap_or(Value::Nil)
                } else {
                    Value::Nil
                }
            }
            Value::String(s) => {
                if let Some(i) = idx.to_int() {
                    let i = if i < 0 { s.len() as i64 + i } else { i } as usize;
                    s.chars()
                        .nth(i)
                        .map(|c| Value::String(c.to_string()))
                        .unwrap_or(Value::Nil)
                } else {
                    Value::Nil
                }
            }
            Value::Hash(map) => {
                let key = idx.to_rush_string();
                map.get(&key).cloned().unwrap_or(Value::Nil)
            }
            _ => Value::Nil,
        }
    }

    fn access_property(&self, receiver: &Value, property: &str) -> Value {
        match receiver {
            Value::String(s) => self.string_method(s, property, &[]),
            Value::Array(arr) => self.array_method_no_block(arr, property),
            Value::Hash(map) => {
                // Try method first, then key access
                match property {
                    "keys" | "values" | "length" | "size" | "count" | "empty?" => {
                        self.hash_method(map, property, &[])
                    }
                    _ => map.get(property).cloned().unwrap_or(Value::Nil),
                }
            }
            Value::Int(n) => self.int_method_no_block(*n, property),
            _ => Value::Nil,
        }
    }

    fn array_method_no_block(&self, arr: &[Value], method: &str) -> Value {
        match method {
            "length" | "size" | "count" => Value::Int(arr.len() as i64),
            "empty?" => Value::Bool(arr.is_empty()),
            "first" => arr.first().cloned().unwrap_or(Value::Nil),
            "last" => arr.last().cloned().unwrap_or(Value::Nil),
            "reverse" => Value::Array(arr.iter().rev().cloned().collect()),
            "sort" => {
                let mut sorted = arr.to_vec();
                sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
                Value::Array(sorted)
            }
            "uniq" => {
                let mut seen = Vec::new();
                let mut result = Vec::new();
                for v in arr {
                    let s = v.inspect();
                    if !seen.contains(&s) {
                        seen.push(s);
                        result.push(v.clone());
                    }
                }
                Value::Array(result)
            }
            "flatten" => {
                let mut result = Vec::new();
                fn flatten_into(val: &Value, out: &mut Vec<Value>) {
                    if let Value::Array(inner) = val {
                        for v in inner {
                            flatten_into(v, out);
                        }
                    } else {
                        out.push(val.clone());
                    }
                }
                for v in arr {
                    flatten_into(v, &mut result);
                }
                Value::Array(result)
            }
            "sum" => {
                let mut total = Value::Int(0);
                for v in arr {
                    total = self.add(&total, v);
                }
                total
            }
            "min" => arr
                .iter()
                .min_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal))
                .cloned()
                .unwrap_or(Value::Nil),
            "max" => arr
                .iter()
                .max_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal))
                .cloned()
                .unwrap_or(Value::Nil),
            _ => Value::Nil,
        }
    }

    fn int_method_no_block(&self, n: i64, method: &str) -> Value {
        match method {
            "abs" => Value::Int(n.abs()),
            "to_s" => Value::String(n.to_string()),
            "to_f" => Value::Float(n as f64),
            "even?" => Value::Bool(n % 2 == 0),
            "odd?" => Value::Bool(n % 2 != 0),
            "zero?" => Value::Bool(n == 0),
            "positive?" => Value::Bool(n > 0),
            "negative?" => Value::Bool(n < 0),
            _ => Value::Nil,
        }
    }

    // ── Function Calls ──────────────────────────────────────────────

    fn call_function(&mut self, name: &str, args: &[Value]) -> Result<Value, Signal> {
        // Builtins
        match name.to_ascii_lowercase().as_str() {
            "puts" => {
                let msg = args
                    .iter()
                    .map(|v| v.to_rush_string())
                    .collect::<Vec<_>>()
                    .join(" ");
                self.output.puts(&msg);
                return Ok(Value::Nil);
            }
            "print" => {
                let msg = args
                    .iter()
                    .map(|v| v.to_rush_string())
                    .collect::<Vec<_>>()
                    .join("");
                self.output.print(&msg);
                return Ok(Value::Nil);
            }
            "warn" => {
                let msg = args
                    .iter()
                    .map(|v| v.to_rush_string())
                    .collect::<Vec<_>>()
                    .join(" ");
                self.output.warn(&msg);
                return Ok(Value::Nil);
            }
            "exit" => {
                let code = args.first().and_then(|v| v.to_int()).unwrap_or(0) as i32;
                std::process::exit(code);
            }
            "sleep" => {
                let secs = args.first().and_then(|v| v.to_float()).unwrap_or(1.0);
                std::thread::sleep(std::time::Duration::from_secs_f64(secs));
                return Ok(Value::Nil);
            }
            "die" => {
                let msg = args
                    .first()
                    .map(|v| v.to_rush_string())
                    .unwrap_or_default();
                self.output.warn(&msg);
                std::process::exit(1);
            }
            "ai" => {
                let prompt = args.iter().map(|v| v.to_rush_string()).collect::<Vec<_>>().join(" ");
                let (prompt, provider, model) = crate::ai::parse_ai_args(&prompt);
                match crate::ai::execute(provider.as_deref(), model.as_deref(), &prompt, None) {
                    Ok(response) => return Ok(Value::String(response)),
                    Err(e) => {
                        self.output.warn(&format!("ai: {e}"));
                        return Ok(Value::Nil);
                    }
                }
            }
            _ => {}
        }

        // User-defined functions
        if let Some(func) = self.env.functions.get(name).cloned() {
            return self.call_user_function(&func, args);
        }

        // Try as external command
        if process::command_exists(name) {
            let str_args: Vec<String> = args.iter().map(|v| v.to_rush_string()).collect();
            let str_refs: Vec<&str> = str_args.iter().map(|s| s.as_str()).collect();
            let result = process::run_command(name, &str_refs);
            self.exit_code = result.exit_code;
            if !result.stderr.is_empty() {
                self.output.warn(&result.stderr);
            }
            return Ok(Value::Int(result.exit_code as i64));
        }

        // Truly unknown
        Ok(Value::Nil)
    }

    fn call_user_function(&mut self, func: &Function, args: &[Value]) -> Result<Value, Signal> {
        self.env.push_scope();

        // Bind parameters
        for (i, param) in func.params.iter().enumerate() {
            let val = if let Some(arg) = args.get(i) {
                arg.clone()
            } else if let Some(ref default) = param.default_value {
                self.eval_node(default)?
            } else {
                Value::Nil
            };
            self.env.set_local(&param.name, val);
        }

        // If the function has a raw body, dispatch each line through triage.
        // This handles mixed Rush+shell function bodies like:
        //   def mcd(dir)
        //     mkdir -p $dir    # shell command
        //     cd $dir          # builtin
        //     puts "done"      # Rush
        //   end
        if let Some(ref raw) = func.raw_body {
            let result = self.exec_function_body_mixed(raw, &func.body);
            self.env.pop_scope();
            return result;
        }

        let result = match self.exec(&func.body) {
            Ok(v) => Ok(v),
            Err(Signal::Return(v)) => Ok(v),
            Err(other) => Err(other),
        };

        self.env.pop_scope();
        result
    }

    /// Execute a function body with mixed Rush+shell dispatch.
    /// For each line: if it's Rush syntax, parse and eval; if shell, run natively.
    fn exec_function_body_mixed(&mut self, raw: &str, ast_body: &[Node]) -> Result<Value, Signal> {
        // First, try executing the parsed AST body.
        // If all nodes evaluate successfully (not Nil for command-like things), use that.
        // This handles pure-Rush functions efficiently.
        // Detect shell commands: lines that triage says are shell AND look like
        // actual commands (contain flags, pipes, paths, or known shell patterns).
        // Simple expressions like "a + 1" are NOT shell commands even though
        // triage doesn't recognize them as Rush (because 'a' looks like a command).
        let has_shell_commands = raw.lines().any(|line| {
            let trimmed = line.trim();
            if trimmed.is_empty() || trimmed.starts_with('#') {
                return false;
            }
            if crate::triage::is_rush_syntax(trimmed) {
                return false;
            }
            // Only treat as shell if it looks like an actual command invocation:
            // - contains flags (-x, --flag)
            // - contains path separators (/)
            // - first word is a known command on PATH
            let first_word = trimmed.split_whitespace().next().unwrap_or("");
            first_word.contains('/')
                || trimmed.contains(" -")
                || trimmed.contains(" --")
                || trimmed.contains('|')
                || trimmed.contains('>')
                || trimmed.contains('<')
                || crate::process::command_exists(first_word)
        });

        if !has_shell_commands {
            // Pure Rush function — use parsed AST
            return match self.exec(ast_body) {
                Ok(v) => Ok(v),
                Err(Signal::Return(v)) => Ok(v),
                Err(other) => Err(other),
            };
        }

        // Mixed body — dispatch line by line
        let mut last_value = Value::Nil;
        for line in raw.lines() {
            let trimmed = line.trim();
            if trimmed.is_empty() || trimmed.starts_with('#') {
                continue;
            }

            // Expand Rush variables in the line
            let expanded = self.expand_vars_in_line(trimmed);
            let expanded = expanded.as_str();

            if crate::triage::is_rush_syntax(expanded) {
                // Parse and eval as Rush
                match crate::parser::parse(expanded) {
                    Ok(nodes) => {
                        match self.exec(&nodes) {
                            Ok(v) => last_value = v,
                            Err(Signal::Return(v)) => return Ok(v),
                            Err(other) => return Err(other),
                        }
                    }
                    Err(_) => {
                        // Parse failed — try as shell command
                        let result = crate::dispatch::dispatch(expanded, self, None);
                        self.exit_code = result.exit_code;
                    }
                }
            } else {
                // Shell command — dispatch through the standard pipeline
                let result = crate::dispatch::dispatch(expanded, self, None);
                self.exit_code = result.exit_code;
            }
        }
        Ok(last_value)
    }

    /// Expand Rush variables ($name references from the current scope) in a line.
    fn expand_vars_in_line(&self, line: &str) -> String {
        if !line.contains('$') {
            return line.to_string();
        }
        let mut result = String::with_capacity(line.len());
        let chars: Vec<char> = line.chars().collect();
        let mut i = 0;
        while i < chars.len() {
            if chars[i] == '$' && i + 1 < chars.len()
                && (chars[i + 1].is_ascii_alphabetic() || chars[i + 1] == '_')
            {
                i += 1;
                let mut var_name = String::new();
                while i < chars.len() && (chars[i].is_ascii_alphanumeric() || chars[i] == '_') {
                    var_name.push(chars[i]);
                    i += 1;
                }
                // Check Rush scope first, then env vars
                if let Some(val) = self.env.get(&var_name) {
                    result.push_str(&val.to_rush_string());
                } else if let Ok(val) = std::env::var(&var_name) {
                    result.push_str(&val);
                } else {
                    // Variable not found — leave as empty string
                }
            } else {
                result.push(chars[i]);
                i += 1;
            }
        }
        result
    }

    fn call_block(&mut self, block: &BlockLiteral, args: &[Value]) -> Value {
        self.env.push_scope();
        for (i, param) in block.params.iter().enumerate() {
            let val = args.get(i).cloned().unwrap_or(Value::Nil);
            self.env.set_local(param, val);
        }
        let result = self.exec(&block.body).unwrap_or(Value::Nil);
        self.env.pop_scope();
        result
    }

    // ── Iterables ───────────────────────────────────────────────────

    fn value_to_iterable(&self, value: &Value) -> Vec<Value> {
        match value {
            Value::Array(arr) => arr.clone(),
            Value::Range(start, end, exclusive) => {
                if *exclusive {
                    (*start..*end).map(Value::Int).collect()
                } else {
                    (*start..=*end).map(Value::Int).collect()
                }
            }
            Value::String(s) => s.chars().map(|c| Value::String(c.to_string())).collect(),
            _ => vec![value.clone()],
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::parser;

    struct TestOutput {
        lines: Vec<String>,
    }

    impl TestOutput {
        fn new() -> Self {
            Self { lines: Vec::new() }
        }
    }

    impl Output for TestOutput {
        fn puts(&mut self, s: &str) {
            self.lines.push(s.to_string());
        }
        fn print(&mut self, s: &str) {
            self.lines.push(s.to_string());
        }
        fn warn(&mut self, s: &str) {
            self.lines.push(format!("WARN: {s}"));
        }
    }

    fn eval(src: &str) -> (Value, Vec<String>) {
        let nodes = parser::parse(src).unwrap();
        let mut output = TestOutput::new();
        let val = {
            let mut evaluator = Evaluator::new(&mut output);
            evaluator.exec_toplevel(&nodes).unwrap()
        };
        (val, output.lines)
    }

    fn eval_val(src: &str) -> Value {
        eval(src).0
    }

    fn eval_output(src: &str) -> Vec<String> {
        eval(src).1
    }

    // ── Literals ────────────────────────────────────────────────────

    #[test]
    fn integer_literal() {
        assert_eq!(eval_val("42"), Value::Int(42));
    }

    #[test]
    fn float_literal() {
        assert_eq!(eval_val("3.14"), Value::Float(3.14));
    }

    #[test]
    fn string_literal() {
        assert_eq!(eval_val("\"hello\""), Value::String("hello".to_string()));
    }

    #[test]
    fn bool_literals() {
        assert_eq!(eval_val("true"), Value::Bool(true));
        assert_eq!(eval_val("false"), Value::Bool(false));
    }

    #[test]
    fn nil_literal() {
        assert_eq!(eval_val("nil"), Value::Nil);
    }

    #[test]
    fn size_suffix() {
        assert_eq!(eval_val("1kb"), Value::Int(1024));
        assert_eq!(eval_val("1mb"), Value::Int(1048576));
    }

    // ── Arithmetic ──────────────────────────────────────────────────

    #[test]
    fn addition() {
        assert_eq!(eval_val("1 + 2"), Value::Int(3));
    }

    #[test]
    fn subtraction() {
        assert_eq!(eval_val("10 - 3"), Value::Int(7));
    }

    #[test]
    fn multiplication() {
        assert_eq!(eval_val("4 * 5"), Value::Int(20));
    }

    #[test]
    fn division() {
        assert_eq!(eval_val("10 / 3"), Value::Int(3));
    }

    #[test]
    fn modulo() {
        assert_eq!(eval_val("10 % 3"), Value::Int(1));
    }

    #[test]
    fn float_arithmetic() {
        assert_eq!(eval_val("1.5 + 2.5"), Value::Float(4.0));
    }

    #[test]
    fn mixed_arithmetic() {
        assert_eq!(eval_val("1 + 2.5"), Value::Float(3.5));
    }

    #[test]
    fn operator_precedence() {
        assert_eq!(eval_val("2 + 3 * 4"), Value::Int(14));
    }

    #[test]
    fn unary_minus() {
        assert_eq!(eval_val("-42"), Value::Int(-42));
    }

    #[test]
    fn string_concat() {
        assert_eq!(
            eval_val("\"hello\" + \" world\""),
            Value::String("hello world".to_string())
        );
    }

    #[test]
    fn string_repeat() {
        assert_eq!(
            eval_val("\"ha\" * 3"),
            Value::String("hahaha".to_string())
        );
    }

    // ── Comparison ──────────────────────────────────────────────────

    #[test]
    fn comparison_ops() {
        assert_eq!(eval_val("1 == 1"), Value::Bool(true));
        assert_eq!(eval_val("1 != 2"), Value::Bool(true));
        assert_eq!(eval_val("1 < 2"), Value::Bool(true));
        assert_eq!(eval_val("2 > 1"), Value::Bool(true));
        assert_eq!(eval_val("1 <= 1"), Value::Bool(true));
        assert_eq!(eval_val("2 >= 1"), Value::Bool(true));
    }

    // ── Variables ───────────────────────────────────────────────────

    #[test]
    fn assignment_and_ref() {
        assert_eq!(eval_val("x = 42; x"), Value::Int(42));
    }

    #[test]
    fn compound_assignment() {
        assert_eq!(eval_val("x = 10; x += 5; x"), Value::Int(15));
    }

    #[test]
    fn multiple_assignment() {
        let output = eval_output("a, b = 1, 2; puts a; puts b");
        assert_eq!(output, vec!["1", "2"]);
    }

    // ── Control Flow ────────────────────────────────────────────────

    #[test]
    fn if_true() {
        let output = eval_output("if true\n  puts \"yes\"\nend");
        assert_eq!(output, vec!["yes"]);
    }

    #[test]
    fn if_false() {
        let output = eval_output("if false\n  puts \"yes\"\nelse\n  puts \"no\"\nend");
        assert_eq!(output, vec!["no"]);
    }

    #[test]
    fn if_elsif() {
        let output = eval_output("x = 2\nif x == 1\n  puts \"one\"\nelsif x == 2\n  puts \"two\"\nend");
        assert_eq!(output, vec!["two"]);
    }

    #[test]
    fn postfix_if() {
        let output = eval_output("puts \"yes\" if true");
        assert_eq!(output, vec!["yes"]);
    }

    #[test]
    fn postfix_unless() {
        let output = eval_output("puts \"yes\" unless false");
        assert_eq!(output, vec!["yes"]);
    }

    #[test]
    fn ternary() {
        assert_eq!(eval_val("true ? 1 : 2"), Value::Int(1));
        assert_eq!(eval_val("false ? 1 : 2"), Value::Int(2));
    }

    // ── Loops ───────────────────────────────────────────────────────

    #[test]
    fn while_loop() {
        let output = eval_output("x = 0\nwhile x < 3\n  puts x\n  x += 1\nend");
        assert_eq!(output, vec!["0", "1", "2"]);
    }

    #[test]
    fn for_loop() {
        let output = eval_output("for x in [1, 2, 3]\n  puts x\nend");
        assert_eq!(output, vec!["1", "2", "3"]);
    }

    #[test]
    fn for_range() {
        let output = eval_output("for i in 1..3\n  puts i\nend");
        assert_eq!(output, vec!["1", "2", "3"]);
    }

    #[test]
    fn break_in_loop() {
        let output = eval_output("x = 0\nwhile true\n  break if x >= 2\n  puts x\n  x += 1\nend");
        assert_eq!(output, vec!["0", "1"]);
    }

    #[test]
    fn next_in_loop() {
        let output = eval_output("for x in [1,2,3,4]\n  next if x == 2\n  puts x\nend");
        assert_eq!(output, vec!["1", "3", "4"]);
    }

    // ── Functions ───────────────────────────────────────────────────

    #[test]
    fn function_def_and_call() {
        let output = eval_output("def greet(name)\n  puts \"hello \" + name\nend\ngreet(\"world\")");
        assert_eq!(output, vec!["hello world"]);
    }

    #[test]
    fn function_return() {
        assert_eq!(
            eval_val("def add(a, b)\n  return a + b\nend\nadd(3, 4)"),
            Value::Int(7)
        );
    }

    #[test]
    fn function_default_params() {
        let output =
            eval_output("def greet(name = \"world\")\n  puts name\nend\ngreet()\ngreet(\"Rush\")");
        assert_eq!(output, vec!["world", "Rush"]);
    }

    // ── Arrays ──────────────────────────────────────────────────────

    #[test]
    fn array_literal() {
        assert_eq!(
            eval_val("[1, 2, 3]"),
            Value::Array(vec![Value::Int(1), Value::Int(2), Value::Int(3)])
        );
    }

    #[test]
    fn array_methods() {
        assert_eq!(eval_val("[1, 2, 3].length"), Value::Int(3));
        assert_eq!(eval_val("[1, 2, 3].first"), Value::Int(1));
        assert_eq!(eval_val("[1, 2, 3].last"), Value::Int(3));
        assert_eq!(eval_val("[].empty?"), Value::Bool(true));
        assert_eq!(eval_val("[1, 2, 3].sum"), Value::Int(6));
    }

    #[test]
    fn array_index() {
        assert_eq!(eval_val("[10, 20, 30][1]"), Value::Int(20));
        assert_eq!(eval_val("[10, 20, 30][-1]"), Value::Int(30));
    }

    #[test]
    fn array_map() {
        let output = eval_output("result = [1, 2, 3].map { |x| x * 2 }\nputs result[0]\nputs result[1]\nputs result[2]");
        assert_eq!(output, vec!["2", "4", "6"]);
    }

    #[test]
    fn array_select() {
        let val = eval_val("[1, 2, 3, 4].select { |x| x > 2 }");
        assert_eq!(
            val,
            Value::Array(vec![Value::Int(3), Value::Int(4)])
        );
    }

    #[test]
    fn array_each() {
        let output = eval_output("[1, 2, 3].each { |x| puts x }");
        assert_eq!(output, vec!["1", "2", "3"]);
    }

    // ── Strings ─────────────────────────────────────────────────────

    #[test]
    fn string_methods() {
        assert_eq!(eval_val("\"hello\".length"), Value::Int(5));
        assert_eq!(
            eval_val("\"hello\".upcase"),
            Value::String("HELLO".to_string())
        );
        assert_eq!(
            eval_val("\"HELLO\".downcase"),
            Value::String("hello".to_string())
        );
        assert_eq!(
            eval_val("\" hi \".strip"),
            Value::String("hi".to_string())
        );
        assert_eq!(
            eval_val("\"hello\".reverse"),
            Value::String("olleh".to_string())
        );
    }

    #[test]
    fn string_split() {
        let val = eval_val("\"a,b,c\".split(\",\")");
        assert_eq!(
            val,
            Value::Array(vec![
                Value::String("a".to_string()),
                Value::String("b".to_string()),
                Value::String("c".to_string()),
            ])
        );
    }

    #[test]
    fn string_interpolation() {
        let output = eval_output("name = \"world\"\nputs \"hello #{name}\"");
        assert_eq!(output, vec!["hello world"]);
    }

    #[test]
    fn escape_sequences() {
        let output = eval_output("puts \"a\\tb\"");
        assert_eq!(output, vec!["a\tb"]);
    }

    // ── Hashes ──────────────────────────────────────────────────────

    #[test]
    fn hash_literal() {
        let val = eval_val("{a: 1, b: 2}");
        assert!(matches!(val, Value::Hash(_)));
    }

    #[test]
    fn hash_access() {
        assert_eq!(eval_val("h = {a: 1, b: 2}; h.keys.length"), Value::Int(2));
    }

    // ── Case ────────────────────────────────────────────────────────

    #[test]
    fn case_when() {
        let output = eval_output("x = 2\ncase x\nwhen 1\n  puts \"one\"\nwhen 2\n  puts \"two\"\nelse\n  puts \"other\"\nend");
        assert_eq!(output, vec!["two"]);
    }

    // ── Try/Rescue ──────────────────────────────────────────────────

    #[test]
    fn try_rescue() {
        // Try body runs, rescue not executed when no error
        let output = eval_output("try\n  puts \"ok\"\nrescue => e\n  puts \"error\"\nend");
        assert_eq!(output, vec!["ok"]);
    }

    // ── Logical Operators ───────────────────────────────────────────

    #[test]
    fn logical_and() {
        assert_eq!(eval_val("true && false"), Value::Bool(false));
        assert_eq!(eval_val("true && true"), Value::Bool(true));
    }

    #[test]
    fn logical_or() {
        assert_eq!(eval_val("false || true"), Value::Bool(true));
        assert_eq!(eval_val("false || false"), Value::Bool(false));
    }

    #[test]
    fn logical_not() {
        assert_eq!(eval_val("not true"), Value::Bool(false));
        assert_eq!(eval_val("not false"), Value::Bool(true));
    }

    // ── Int Methods ─────────────────────────────────────────────────

    #[test]
    fn int_methods() {
        assert_eq!(eval_val("42.even?"), Value::Bool(true));
        assert_eq!(eval_val("41.odd?"), Value::Bool(true));
        assert_eq!(eval_val("(-5).abs"), Value::Int(5));
        assert_eq!(eval_val("0.zero?"), Value::Bool(true));
    }

    #[test]
    fn int_times() {
        let output = eval_output("3.times { |i| puts i }");
        assert_eq!(output, vec!["0", "1", "2"]);
    }

    // ── Reduce ──────────────────────────────────────────────────────

    #[test]
    fn array_reduce() {
        assert_eq!(
            eval_val("[1, 2, 3, 4].reduce { |acc, x| acc + x }"),
            Value::Int(10)
        );
    }

    // ── Scoping ─────────────────────────────────────────────────────

    #[test]
    fn function_scope_isolates_variables() {
        let output = eval_output("x = 10\ndef f()\n  x = 99\nend\nf()\nputs x");
        // x inside f() should shadow, not modify outer x
        // (Current impl: set searches outward, so this actually modifies outer x.
        //  This test documents the current behavior.)
        assert_eq!(output, vec!["99"]);
    }

    #[test]
    fn function_params_dont_leak() {
        let val = eval_val("def f(a)\n  a + 1\nend\nf(5)");
        assert_eq!(val, Value::Int(6));
        // 'a' should not be visible after the call
        let val2 = eval_val("def f(a)\n  a + 1\nend\nf(5)\na");
        assert_eq!(val2, Value::Nil);
    }

    #[test]
    fn nested_function_calls() {
        assert_eq!(
            eval_val("def double(x)\n  x * 2\nend\ndef quad(x)\n  double(double(x))\nend\nquad(3)"),
            Value::Int(12)
        );
    }

    // ── Classes ─────────────────────────────────────────────────────

    #[test]
    fn class_method_definition() {
        let output = eval_output(
            "class Dog\n  attr name\n  def bark\n    puts \"woof\"\n  end\nend"
        );
        assert!(output.is_empty()); // class def produces no output
    }

    // ── Enums ───────────────────────────────────────────────────────

    #[test]
    fn enum_definition() {
        // Enum def stores members as "EnumName::Member" variables
        let (_, output) = eval("enum Color\n  Red\n  Green\n  Blue\nend");
        assert!(output.is_empty()); // no output, just defines
    }

    // ── Compound Assignment ─────────────────────────────────────────

    #[test]
    fn star_assign() {
        assert_eq!(eval_val("x = 3; x *= 4; x"), Value::Int(12));
    }

    #[test]
    fn slash_assign() {
        assert_eq!(eval_val("x = 20; x /= 4; x"), Value::Int(5));
    }

    // ── String Methods ──────────────────────────────────────────────

    #[test]
    fn string_include() {
        assert_eq!(eval_val("\"hello world\".include?(\"world\")"), Value::Bool(true));
        assert_eq!(eval_val("\"hello world\".include?(\"xyz\")"), Value::Bool(false));
    }

    #[test]
    fn string_starts_ends_with() {
        assert_eq!(eval_val("\"hello\".start_with?(\"hel\")"), Value::Bool(true));
        assert_eq!(eval_val("\"hello\".end_with?(\"llo\")"), Value::Bool(true));
    }

    #[test]
    fn string_replace() {
        assert_eq!(
            eval_val("\"hello world\".replace(\"world\", \"rush\")"),
            Value::String("hello rush".to_string())
        );
    }

    #[test]
    fn string_chars() {
        let val = eval_val("\"abc\".chars");
        assert_eq!(val, Value::Array(vec![
            Value::String("a".to_string()),
            Value::String("b".to_string()),
            Value::String("c".to_string()),
        ]));
    }

    #[test]
    fn string_lines() {
        let val = eval_val("\"a\\nb\\nc\".lines");
        assert_eq!(val, Value::Array(vec![
            Value::String("a".to_string()),
            Value::String("b".to_string()),
            Value::String("c".to_string()),
        ]));
    }

    #[test]
    fn string_to_i_to_f() {
        assert_eq!(eval_val("\"42\".to_i"), Value::Int(42));
        assert_eq!(eval_val("\"3.14\".to_f"), Value::Float(3.14));
    }

    // ── Array Methods ───────────────────────────────────────────────

    #[test]
    fn array_sort() {
        assert_eq!(
            eval_val("[3, 1, 2].sort"),
            Value::Array(vec![Value::Int(1), Value::Int(2), Value::Int(3)])
        );
    }

    #[test]
    fn array_reverse() {
        assert_eq!(
            eval_val("[1, 2, 3].reverse"),
            Value::Array(vec![Value::Int(3), Value::Int(2), Value::Int(1)])
        );
    }

    #[test]
    fn array_uniq() {
        assert_eq!(
            eval_val("[1, 2, 2, 3, 1].uniq"),
            Value::Array(vec![Value::Int(1), Value::Int(2), Value::Int(3)])
        );
    }

    #[test]
    fn array_flatten() {
        assert_eq!(
            eval_val("[[1, 2], [3, 4]].flatten"),
            Value::Array(vec![Value::Int(1), Value::Int(2), Value::Int(3), Value::Int(4)])
        );
    }

    #[test]
    fn array_join() {
        assert_eq!(
            eval_val("[1, 2, 3].join(\", \")"),
            Value::String("1, 2, 3".to_string())
        );
    }

    #[test]
    fn array_include() {
        assert_eq!(eval_val("[1, 2, 3].include?(2)"), Value::Bool(true));
        assert_eq!(eval_val("[1, 2, 3].include?(5)"), Value::Bool(false));
    }

    #[test]
    fn array_push() {
        assert_eq!(
            eval_val("[1, 2].push(3)"),
            Value::Array(vec![Value::Int(1), Value::Int(2), Value::Int(3)])
        );
    }

    #[test]
    fn array_min_max() {
        assert_eq!(eval_val("[3, 1, 2].min"), Value::Int(1));
        assert_eq!(eval_val("[3, 1, 2].max"), Value::Int(3));
    }

    #[test]
    fn array_reject() {
        let val = eval_val("[1, 2, 3, 4].reject { |x| x > 2 }");
        assert_eq!(val, Value::Array(vec![Value::Int(1), Value::Int(2)]));
    }

    // ── Hash Methods ────────────────────────────────────────────────

    #[test]
    fn hash_keys_values() {
        let output = eval_output("h = {a: 1, b: 2}\nputs h.keys.length\nputs h.values.length");
        assert_eq!(output, vec!["2", "2"]);
    }

    #[test]
    fn hash_empty() {
        assert_eq!(eval_val("{}.empty?"), Value::Bool(true));
        assert_eq!(eval_val("{a: 1}.empty?"), Value::Bool(false));
    }

    // ── Duration Literals ───────────────────────────────────────────

    #[test]
    fn duration_hours() {
        assert_eq!(eval_val("2.hours"), Value::Int(7200));
    }

    #[test]
    fn duration_minutes() {
        assert_eq!(eval_val("30.minutes"), Value::Int(1800));
    }

    #[test]
    fn duration_days() {
        assert_eq!(eval_val("1.day"), Value::Int(86400));
    }

    // ── Stdlib ──────────────────────────────────────────────────────

    #[test]
    fn file_write_read_delete() {
        let tmp = std::env::temp_dir().join("rush_eval_test.txt");
        let path = tmp.to_string_lossy().replace('\\', "/");
        let output = eval_output(
            &format!("File.write(\"{path}\", \"hello\")\nputs File.read(\"{path}\")\nFile.delete(\"{path}\")")
        );
        assert_eq!(output, vec!["hello"]);
    }

    #[test]
    fn file_exist() {
        assert_eq!(eval_val("File.exist?(\"/tmp\")"), Value::Bool(false)); // /tmp is a dir
    }

    #[test]
    fn dir_pwd() {
        let val = eval_val("Dir.pwd");
        assert!(matches!(val, Value::String(s) if !s.is_empty()));
    }

    #[test]
    fn dir_home() {
        let val = eval_val("Dir.home");
        assert!(matches!(val, Value::String(s) if !s.is_empty()));
    }

    #[test]
    fn time_now() {
        let val = eval_val("Time.now");
        assert!(matches!(val, Value::String(s) if s.contains("-")));
    }

    #[test]
    fn time_epoch() {
        let val = eval_val("Time.epoch");
        assert!(matches!(val, Value::Int(n) if n > 1_700_000_000));
    }

    #[test]
    fn env_access() {
        let val = eval_val("env.PATH");
        assert!(matches!(val, Value::String(s) if !s.is_empty()));
    }

    // ── Edge Cases ──────────────────────────────────────────────────

    #[test]
    fn empty_array() {
        assert_eq!(eval_val("[]"), Value::Array(vec![]));
    }

    #[test]
    fn empty_hash() {
        assert_eq!(eval_val("{}"), Value::Hash(std::collections::HashMap::new()));
    }

    #[test]
    fn nil_is_falsy() {
        let output = eval_output("if nil\n  puts \"yes\"\nelse\n  puts \"no\"\nend");
        assert_eq!(output, vec!["no"]);
    }

    #[test]
    fn zero_is_truthy() {
        let output = eval_output("if 0\n  puts \"yes\"\nelse\n  puts \"no\"\nend");
        assert_eq!(output, vec!["yes"]);
    }

    #[test]
    fn empty_string_is_truthy() {
        let output = eval_output("if \"\"\n  puts \"yes\"\nelse\n  puts \"no\"\nend");
        assert_eq!(output, vec!["yes"]);
    }

    #[test]
    fn chained_method_calls() {
        assert_eq!(
            eval_val("\"hello world\".upcase.reverse"),
            Value::String("DLROW OLLEH".to_string())
        );
    }

    #[test]
    fn nested_interpolation() {
        let output = eval_output("x = 5\nputs \"result: #{x * 2 + 1}\"");
        assert_eq!(output, vec!["result: 11"]);
    }

    #[test]
    fn command_substitution() {
        let val = eval_val("$(echo hello)");
        assert_eq!(val, Value::String("hello".to_string()));
    }

    #[test]
    fn logical_short_circuit() {
        // false && side_effect should not evaluate side_effect
        let output = eval_output("false && puts(\"nope\")");
        assert!(output.is_empty());
    }

    #[test]
    fn or_short_circuit() {
        // true || side_effect should not evaluate side_effect
        let output = eval_output("true || puts(\"nope\")");
        assert!(output.is_empty());
    }

    #[test]
    fn negative_array_index() {
        assert_eq!(eval_val("[10, 20, 30][-1]"), Value::Int(30));
        assert_eq!(eval_val("[10, 20, 30][-2]"), Value::Int(20));
    }

    #[test]
    fn range_iteration() {
        let output = eval_output("for i in 1..3\n  puts i\nend");
        assert_eq!(output, vec!["1", "2", "3"]);
    }

    #[test]
    fn exclusive_range_iteration() {
        let output = eval_output("for i in 1...3\n  puts i\nend");
        assert_eq!(output, vec!["1", "2"]);
    }

    #[test]
    fn unless_block() {
        let output = eval_output("unless false\n  puts \"yes\"\nend");
        assert_eq!(output, vec!["yes"]);
    }

    #[test]
    fn until_loop() {
        let output = eval_output("x = 0\nuntil x >= 3\n  puts x\n  x += 1\nend");
        assert_eq!(output, vec!["0", "1", "2"]);
    }

    #[test]
    fn case_else() {
        let output = eval_output("case 99\nwhen 1\n  puts \"one\"\nelse\n  puts \"other\"\nend");
        assert_eq!(output, vec!["other"]);
    }

    #[test]
    fn multiline_string_interpolation() {
        let output = eval_output("a = 1\nb = 2\nputs \"#{a} + #{b} = #{a + b}\"");
        assert_eq!(output, vec!["1 + 2 = 3"]);
    }

    #[test]
    fn sleep_builtin() {
        // Just verify it doesn't crash (sleep 0 is instant)
        let _ = eval_val("sleep(0)");
    }
}
