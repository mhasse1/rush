//! Shell builtins that must run in-process (cd, export, source, alias, etc.)

use rush_core::eval::Evaluator;
use std::cell::RefCell;
use std::collections::HashMap;

// ── Thread-local state ──────────────────────────────────────────────

thread_local! {
    // cd - : previous directory
    static OLDPWD: RefCell<Option<String>> = const { RefCell::new(None) };
    // pushd/popd directory stack
    static DIR_STACK: RefCell<Vec<String>> = const { RefCell::new(Vec::new()) };
    // alias registry: name → expansion
    static ALIASES: RefCell<HashMap<String, String>> = RefCell::new(HashMap::new());
}

/// Try to handle a line as a shell builtin. Returns true if handled.
pub fn handle(evaluator: &mut Evaluator, line: &str) -> bool {
    // Expand aliases first
    let expanded = expand_alias(line);
    let line = expanded.as_deref().unwrap_or(line);

    let parts: Vec<&str> = line.splitn(2, char::is_whitespace).collect();
    let cmd = parts[0];
    let args = parts.get(1).map(|s| s.trim()).unwrap_or("");

    match cmd {
        "cd" => { handle_cd(evaluator, args); true }
        "export" => { handle_export(args); true }
        "unset" => { handle_unset(args); true }
        "source" | "." => { handle_source(evaluator, args); true }
        "clear" => { print!("\x1b[2J\x1b[H"); true }
        "exit" | "quit" => std::process::exit(evaluator.exit_code),
        "pwd" => { println!("{}", std::env::current_dir().unwrap_or_default().display()); true }
        "alias" => { handle_alias(args); true }
        "unalias" => { handle_unalias(args); true }
        "pushd" => { handle_pushd(evaluator, args); true }
        "popd" => { handle_popd(evaluator); true }
        "dirs" => { handle_dirs(); true }
        "history" => { handle_history(args); true }
        "path" => { handle_path(args); true }
        "help" => { handle_help(evaluator, args); true }
        "set" => { handle_set(args); true }
        _ => false,
    }
}

// ── cd ──────────────────────────────────────────────────────────────

fn handle_cd(evaluator: &mut Evaluator, target: &str) {
    let current = cwd_string();

    let path = if target.is_empty() || target == "~" {
        home_dir()
    } else if target == "-" {
        let prev = OLDPWD.with(|p| p.borrow().clone()).unwrap_or_else(home_dir);
        println!("{prev}");
        prev
    } else if let Some(rest) = target.strip_prefix("~/") {
        format!("{}/{rest}", home_dir())
    } else {
        target.to_string()
    };

    match std::env::set_current_dir(&path) {
        Ok(()) => {
            OLDPWD.with(|p| *p.borrow_mut() = Some(current));
            evaluator.exit_code = 0;
        }
        Err(e) => {
            eprintln!("cd: {path}: {e}");
            evaluator.exit_code = 1;
        }
    }
}

// ── export / unset ──────────────────────────────────────────────────

fn handle_export(args: &str) {
    if args.is_empty() {
        let mut vars: Vec<(String, String)> = std::env::vars().collect();
        vars.sort_by(|a, b| a.0.cmp(&b.0));
        for (k, v) in vars {
            println!("export {k}={v}");
        }
        return;
    }
    if let Some((key, value)) = args.split_once('=') {
        let key = key.trim();
        let value = value.trim().trim_matches('"').trim_matches('\'');
        unsafe { std::env::set_var(key, value) };
    } else {
        eprintln!("export: usage: export NAME=VALUE");
    }
}

fn handle_unset(args: &str) {
    for name in args.split_whitespace() {
        unsafe { std::env::remove_var(name) };
    }
}

// ── source ──────────────────────────────────────────────────────────

fn handle_source(evaluator: &mut Evaluator, path: &str) {
    if path.is_empty() {
        eprintln!("source: usage: source <file>");
        return;
    }
    let expanded = expand_tilde(path);
    match std::fs::read_to_string(&expanded) {
        Ok(content) => run_script_with_builtins(evaluator, &content, &expanded),
        Err(e) => eprintln!("source: {expanded}: {e}"),
    }
}

// ── alias / unalias ─────────────────────────────────────────────────

fn handle_alias(args: &str) {
    if args.is_empty() {
        // Show all aliases
        ALIASES.with(|a| {
            let aliases = a.borrow();
            let mut sorted: Vec<_> = aliases.iter().collect();
            sorted.sort_by_key(|(k, _)| (*k).clone());
            for (name, expansion) in sorted {
                println!("alias {name}='{expansion}'");
            }
        });
        return;
    }

    // alias name='expansion' or alias name=expansion
    if let Some((name, expansion)) = args.split_once('=') {
        let name = name.trim().to_string();
        let expansion = expansion.trim().trim_matches('\'').trim_matches('"').to_string();

        // --save: persist to config
        let save = expansion.contains("--save") || args.contains("--save");
        let expansion = expansion.replace("--save", "").trim().to_string();

        ALIASES.with(|a| {
            a.borrow_mut().insert(name.clone(), expansion.clone());
        });

        if save {
            save_alias_to_config(&name, &expansion);
        }
    } else {
        // alias name — show specific alias
        let name = args.trim();
        ALIASES.with(|a| {
            if let Some(exp) = a.borrow().get(name) {
                println!("alias {name}='{exp}'");
            } else {
                eprintln!("alias: {name}: not found");
            }
        });
    }
}

fn handle_unalias(args: &str) {
    for name in args.split_whitespace() {
        ALIASES.with(|a| {
            a.borrow_mut().remove(name);
        });
    }
}

fn expand_alias(line: &str) -> Option<String> {
    let first_word = line.split_whitespace().next()?;
    ALIASES.with(|a| {
        let aliases = a.borrow();
        aliases.get(first_word).map(|expansion| {
            let rest = line[first_word.len()..].to_string();
            format!("{expansion}{rest}")
        })
    })
}

fn save_alias_to_config(name: &str, expansion: &str) {
    let config_path = config_dir().join("config.json");
    let mut config: serde_json::Value = if config_path.exists() {
        std::fs::read_to_string(&config_path)
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or(serde_json::json!({}))
    } else {
        serde_json::json!({})
    };

    let aliases = config.as_object_mut().unwrap()
        .entry("aliases")
        .or_insert(serde_json::json!({}));
    aliases[name] = serde_json::Value::String(expansion.to_string());

    if let Ok(json) = serde_json::to_string_pretty(&config) {
        std::fs::write(&config_path, json).ok();
    }
}

// ── pushd / popd / dirs ─────────────────────────────────────────────

fn handle_pushd(evaluator: &mut Evaluator, target: &str) {
    let current = cwd_string();

    let path = if target.is_empty() {
        // pushd with no args: swap top two
        let top = DIR_STACK.with(|s| s.borrow_mut().pop());
        match top {
            Some(dir) => {
                DIR_STACK.with(|s| s.borrow_mut().push(current.clone()));
                dir
            }
            None => {
                eprintln!("pushd: no other directory");
                return;
            }
        }
    } else {
        DIR_STACK.with(|s| s.borrow_mut().push(current));
        expand_tilde(target)
    };

    match std::env::set_current_dir(&path) {
        Ok(()) => {
            evaluator.exit_code = 0;
            handle_dirs();
        }
        Err(e) => {
            eprintln!("pushd: {path}: {e}");
            // Restore stack
            DIR_STACK.with(|s| { s.borrow_mut().pop(); });
            evaluator.exit_code = 1;
        }
    }
}

fn handle_popd(evaluator: &mut Evaluator) {
    let dir = DIR_STACK.with(|s| s.borrow_mut().pop());
    match dir {
        Some(path) => {
            match std::env::set_current_dir(&path) {
                Ok(()) => {
                    evaluator.exit_code = 0;
                    handle_dirs();
                }
                Err(e) => {
                    eprintln!("popd: {path}: {e}");
                    evaluator.exit_code = 1;
                }
            }
        }
        None => {
            eprintln!("popd: directory stack empty");
            evaluator.exit_code = 1;
        }
    }
}

fn handle_dirs() {
    let cwd = cwd_string();
    let stack = DIR_STACK.with(|s| s.borrow().clone());
    print!("{cwd}");
    for dir in stack.iter().rev() {
        print!(" {dir}");
    }
    println!();
}

// ── history ─────────────────────────────────────────────────────────

fn handle_history(args: &str) {
    if args == "-c" || args == "--clear" {
        let path = config_dir().join("history");
        std::fs::write(&path, "").ok();
        println!("History cleared.");
        return;
    }

    let path = config_dir().join("history");
    if let Ok(content) = std::fs::read_to_string(&path) {
        let lines: Vec<&str> = content.lines().collect();
        let n: usize = args.parse().unwrap_or(lines.len());
        let start = lines.len().saturating_sub(n);
        for (i, line) in lines[start..].iter().enumerate() {
            println!("{:5}  {line}", start + i + 1);
        }
    }
}

// ── path ────────────────────────────────────────────────────────────

fn handle_path(args: &str) {
    let parts: Vec<&str> = args.split_whitespace().collect();
    let subcmd = parts.first().copied().unwrap_or("");

    match subcmd {
        "" | "show" => {
            // path — show current PATH
            let path = std::env::var("PATH").unwrap_or_default();
            let sep = if cfg!(windows) { ';' } else { ':' };
            let mut seen = std::collections::HashSet::new();
            for dir in path.split(sep) {
                let exists = std::path::Path::new(dir).is_dir();
                let dup = !seen.insert(dir.to_string());
                let mark = if !exists {
                    "✗"
                } else if dup {
                    "↑"
                } else {
                    "✓"
                };
                println!("  {mark} {dir}");
            }
        }
        "add" => {
            let dirs: Vec<&str> = parts[1..].iter()
                .filter(|d| **d != "--save")
                .copied()
                .collect();
            let save = parts.contains(&"--save");

            if dirs.is_empty() {
                eprintln!("path add: missing directory");
                return;
            }

            let current = std::env::var("PATH").unwrap_or_default();
            let sep = if cfg!(windows) { ";" } else { ":" };
            for dir in &dirs {
                let expanded = expand_tilde(dir);
                let new_path = format!("{current}{sep}{expanded}");
                unsafe { std::env::set_var("PATH", &new_path) };
            }

            if save {
                eprintln!("path add --save: persistence not yet implemented");
            }
        }
        "rm" | "remove" => {
            let target = parts.get(1).copied().unwrap_or("");
            if target.is_empty() {
                eprintln!("path rm: missing directory");
                return;
            }
            let expanded = expand_tilde(target);
            let current = std::env::var("PATH").unwrap_or_default();
            let sep = if cfg!(windows) { ';' } else { ':' };
            let new_path: Vec<&str> = current.split(sep)
                .filter(|d| *d != expanded)
                .collect();
            unsafe { std::env::set_var("PATH", new_path.join(&sep.to_string())) };
        }
        "check" => {
            handle_path(""); // same as show
        }
        "dedupe" => {
            let current = std::env::var("PATH").unwrap_or_default();
            let sep = if cfg!(windows) { ';' } else { ':' };
            let mut seen = std::collections::HashSet::new();
            let deduped: Vec<&str> = current.split(sep)
                .filter(|d| seen.insert(d.to_string()))
                .collect();
            let removed = current.split(sep).count() - deduped.len();
            unsafe { std::env::set_var("PATH", deduped.join(&sep.to_string())) };
            println!("Removed {removed} duplicate(s).");
        }
        _ => eprintln!("path: unknown subcommand '{subcmd}'. Try: add, rm, check, dedupe"),
    }
}

// ── help ────────────────────────────────────────────────────────────

fn handle_help(_evaluator: &mut Evaluator, topic: &str) {
    if topic.is_empty() {
        println!("Rush — a Unix-style shell\n");
        println!("Builtins:");
        println!("  cd [dir]         Change directory (cd -, cd ~)");
        println!("  export K=V       Set environment variable");
        println!("  unset NAME       Remove environment variable");
        println!("  alias k='v'      Define alias (--save to persist)");
        println!("  unalias name     Remove alias");
        println!("  source file      Run script in current session");
        println!("  pushd dir        Push directory onto stack");
        println!("  popd             Pop directory from stack");
        println!("  dirs             Show directory stack");
        println!("  history [n]      Show command history");
        println!("  path [cmd]       PATH management (add, rm, check, dedupe)");
        println!("  set [option]     Show/change settings");
        println!("  help [topic]     Show help");
        println!("  clear            Clear screen");
        println!("  exit             Exit shell");
        println!();
        println!("Topics: variables, strings, arrays, hashes, control-flow,");
        println!("        functions, classes, file, dir, time, pipes, ai");
        println!();
        println!("Use 'help <topic>' for details.");
        return;
    }

    match topic.to_lowercase().as_str() {
        "variables" | "vars" => {
            println!("Variables:");
            println!("  x = 42                    Assignment");
            println!("  a, b = 1, 2               Multiple assignment");
            println!("  x += 1                    Compound (+=, -=, *=, /=)");
            println!("  $os, $arch, $hostname      Built-in variables");
            println!("  $?                         Last exit code");
            println!("  env.HOME                   Environment variables");
        }
        "strings" | "string" => {
            println!("Strings:");
            println!("  \"hello #{{name}}\"            Interpolation");
            println!("  'raw string'               No interpolation");
            println!("  .upcase .downcase           Case conversion");
            println!("  .strip .split .replace     Manipulation");
            println!("  .length .empty? .include?  Queries");
            println!("  .start_with? .end_with?    Prefix/suffix check");
            println!("  .chars .lines .to_i .to_f  Conversion");
        }
        "arrays" | "array" => {
            println!("Arrays:");
            println!("  [1, 2, 3]                  Literal");
            println!("  arr[0]  arr[-1]            Index (negative from end)");
            println!("  .length .empty? .first .last");
            println!("  .push .sort .reverse .uniq .flatten");
            println!("  .map {{|x| ...}} .select {{|x| ...}}");
            println!("  .reject .each .reduce .join");
            println!("  .sum .min .max .include?");
        }
        "hashes" | "hash" => {
            println!("Hashes:");
            println!("  {{a: 1, b: 2}}               Literal");
            println!("  h.keys  h.values            Access");
            println!("  h.length  h.empty?          Queries");
        }
        "control-flow" | "control" | "flow" | "if" | "loops" => {
            println!("Control Flow:");
            println!("  if cond ... elsif ... else ... end");
            println!("  unless cond ... end");
            println!("  while cond ... end");
            println!("  until cond ... end");
            println!("  for x in collection ... end");
            println!("  loop ... end");
            println!("  case expr / when val ... / else ... / end");
            println!("  break, next, return");
            println!("  puts \"x\" if condition       Postfix if/unless");
        }
        "functions" | "function" | "def" => {
            println!("Functions:");
            println!("  def name(arg1, arg2 = default)");
            println!("    body");
            println!("  end");
            println!("  name(args)                  Call with parens");
            println!("  puts \"hello\"                Builtins without parens");
        }
        "classes" | "class" => {
            println!("Classes:");
            println!("  class Name < Parent");
            println!("    attr field1, field2");
            println!("    def initialize(args) ... end");
            println!("    def method(args) ... end");
            println!("  end");
        }
        "file" => {
            println!("File stdlib:");
            println!("  File.read(path)             Read file contents");
            println!("  File.write(path, content)   Write file");
            println!("  File.append(path, content)  Append to file");
            println!("  File.exist?(path)           Check existence");
            println!("  File.delete(path)           Delete file");
            println!("  File.size(path)             File size in bytes");
            println!("  File.copy(src, dst)         Copy file");
            println!("  File.move(src, dst)         Move/rename file");
            println!("  File.basename(path)         File name");
            println!("  File.dirname(path)          Directory name");
            println!("  File.ext(path)              Extension");
        }
        "dir" => {
            println!("Dir stdlib:");
            println!("  Dir.list(path)              List directory");
            println!("  Dir.list(path, :files)      Files only");
            println!("  Dir.list(path, :dirs)       Directories only");
            println!("  Dir.exist?(path)            Check existence");
            println!("  Dir.mkdir(path)             Create directory");
            println!("  Dir.rmdir(path)             Remove directory");
            println!("  Dir.pwd                     Current directory");
            println!("  Dir.home                    Home directory");
            println!("  Dir.glob(pattern)           Glob matching");
        }
        "time" => {
            println!("Time stdlib:");
            println!("  Time.now                    Current local time");
            println!("  Time.utc_now                Current UTC time");
            println!("  Time.today                  Today's date");
            println!("  Time.epoch                  Unix timestamp");
            println!();
            println!("Duration literals:");
            println!("  2.hours    → 7200           Seconds");
            println!("  30.minutes → 1800");
            println!("  1.day      → 86400");
        }
        "pipes" | "pipeline" => {
            println!("Pipeline operators (after |):");
            println!("  where field op value        Filter rows");
            println!("  where /pattern/             Regex filter");
            println!("  select field1, field2       Project fields");
            println!("  sort [field] [--desc]       Sort");
            println!("  count                       Count items");
            println!("  first [n]  last [n]         Take first/last");
            println!("  skip n                      Skip first n");
            println!("  sum  avg  min  max          Aggregation");
            println!("  distinct                    Remove duplicates");
            println!("  as json  as csv             Format output");
            println!("  from json  from csv         Parse input");
            println!("  objectify                   Text table → objects");
            println!("  grep pattern                String filter");
        }
        "ai" => {
            println!("AI integration:");
            println!("  ai \"question\"                Ask with default provider");
            println!("  ai -p openai \"question\"     Specify provider");
            println!("  ai -m gpt-4 \"question\"      Specify model");
            println!();
            println!("Providers: anthropic, openai, gemini, ollama");
            println!("Set API keys: export ANTHROPIC_API_KEY=...");
        }
        _ => {
            eprintln!("help: unknown topic '{topic}'");
            eprintln!("Available: variables, strings, arrays, hashes, control-flow,");
            eprintln!("           functions, classes, file, dir, time, pipes, ai");
        }
    }
}

// ── set ─────────────────────────────────────────────────────────────

fn handle_set(args: &str) {
    let mut config = rush_core::config::RushConfig::load();

    if args.is_empty() {
        config.display();
        return;
    }

    // set -e / set +e / set -x / set +x shortcuts
    match args {
        "-e" => { config.set("stop_on_error", "true"); }
        "+e" => { config.set("stop_on_error", "false"); }
        "-x" => { config.set("trace_commands", "true"); }
        "+x" => { config.set("trace_commands", "false"); }
        "vi" => { config.set("vi", ""); }
        "emacs" => { config.set("emacs", ""); }
        _ => {
            // set key value
            let parts: Vec<&str> = args.splitn(2, char::is_whitespace).collect();
            let key = parts[0];
            let value = parts.get(1).unwrap_or(&"");
            if !config.set(key, value) {
                eprintln!("set: unknown option '{key}'");
                return;
            }
        }
    }

    if let Err(e) = config.save() {
        eprintln!("set: failed to save config: {e}");
    }
}

// ── init.rush loading ───────────────────────────────────────────────

pub fn load_init(evaluator: &mut Evaluator) {
    let init_path = config_dir().join("init.rush");
    if init_path.exists() {
        if let Ok(content) = std::fs::read_to_string(&init_path) {
            run_script_with_builtins(evaluator, &content, "init.rush");
        }
    }
}

/// Run a script handling shell builtins (path, export, alias) line-by-line,
/// and batching Rush syntax for the parser.
fn run_script_with_builtins(evaluator: &mut Evaluator, content: &str, source_name: &str) {
    let mut rush_buf = String::new();

    for line in content.lines() {
        let trimmed = line.trim();

        // Comments → pass to Rush buf (parser skips them)
        if trimmed.starts_with('#') {
            rush_buf.push_str(line);
            rush_buf.push('\n');
            continue;
        }

        // Blank lines → flush Rush buf (isolate parse errors per block)
        if trimmed.is_empty() {
            flush_rush_buf(evaluator, &rush_buf, source_name);
            rush_buf.clear();
            continue;
        }

        // Lines that are shell builtins — handle directly
        let first_word = trimmed.split_whitespace().next().unwrap_or("");
        if matches!(first_word, "path" | "export" | "unset" | "alias" | "cd" | "source" | "clear") {
            // Flush any accumulated Rush code first
            flush_rush_buf(evaluator, &rush_buf, source_name);
            rush_buf.clear();
            // Handle the builtin line
            handle(evaluator, trimmed);
        } else {
            rush_buf.push_str(line);
            rush_buf.push('\n');
        }
    }

    // Flush remaining Rush code
    flush_rush_buf(evaluator, &rush_buf, source_name);
}

fn flush_rush_buf(evaluator: &mut Evaluator, buf: &str, source_name: &str) {
    let trimmed = buf.trim();
    if trimmed.is_empty() {
        return;
    }
    match rush_core::parser::parse(trimmed) {
        Ok(nodes) => {
            if let Err(e) = evaluator.exec_toplevel(&nodes) {
                eprintln!("{source_name}: {e}");
            }
        }
        Err(e) => {
            // Don't abort the whole init.rush on a single parse error
            eprintln!("{source_name}: {e}");
        }
    }
}

pub fn load_secrets(_evaluator: &mut Evaluator) {
    let secrets_path = config_dir().join("secrets.rush");
    if secrets_path.exists() {
        if let Ok(content) = std::fs::read_to_string(&secrets_path) {
            for line in content.lines() {
                let line = line.trim();
                if line.is_empty() || line.starts_with('#') {
                    continue;
                }
                let export_line = line.strip_prefix("export ").unwrap_or(line);
                if let Some((key, value)) = export_line.split_once('=') {
                    let key = key.trim();
                    let value = value.trim().trim_matches('"').trim_matches('\'');
                    unsafe { std::env::set_var(key, value) };
                }
            }
        }
    }
}

/// Load aliases from config.json
pub fn load_aliases_from_config() {
    let config_path = config_dir().join("config.json");
    if let Ok(content) = std::fs::read_to_string(&config_path) {
        if let Ok(config) = serde_json::from_str::<serde_json::Value>(&content) {
            if let Some(aliases) = config.get("aliases").and_then(|a| a.as_object()) {
                ALIASES.with(|a| {
                    let mut map = a.borrow_mut();
                    for (name, expansion) in aliases {
                        if let Some(exp) = expansion.as_str() {
                            map.insert(name.clone(), exp.to_string());
                        }
                    }
                });
            }
        }
    }
}

// ── Inject built-in variables ───────────────────────────────────────

pub fn inject_builtin_vars(evaluator: &mut Evaluator) {
    use rush_core::value::Value;
    evaluator.env.set("$os", Value::String(std::env::consts::OS.to_string()));
    evaluator.env.set("$arch", Value::String(std::env::consts::ARCH.to_string()));
    evaluator.env.set("$hostname", Value::String(rush_core::llm::get_hostname()));
    evaluator.env.set("$user", Value::String(rush_core::llm::get_username()));
    evaluator.env.set("$rush_version", Value::String("0.1.0".to_string()));
    evaluator.env.set("$pid", Value::Int(std::process::id() as i64));
}

// ── Helpers ─────────────────────────────────────────────────────────

fn home_dir() -> String {
    std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_else(|_| ".".to_string())
}

fn config_dir() -> std::path::PathBuf {
    std::path::PathBuf::from(home_dir()).join(".config").join("rush")
}

fn cwd_string() -> String {
    std::env::current_dir()
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_default()
}

fn expand_tilde(path: &str) -> String {
    if let Some(rest) = path.strip_prefix("~/") {
        format!("{}/{rest}", home_dir())
    } else if path == "~" {
        home_dir()
    } else {
        path.to_string()
    }
}
