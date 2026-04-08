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
    // job table for background processes
    pub(crate) static JOB_TABLE: RefCell<rush_core::jobs::JobTable> = RefCell::new(rush_core::jobs::JobTable::new());
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
        "setbg" => { handle_setbg(args); true }
        ".." => { handle_cd(evaluator, ".."); true }
        "..." => { handle_cd(evaluator, "../.."); true }
        "...." => { handle_cd(evaluator, "../../.."); true }
        "which" | "type" => { handle_which(args); true }
        ":" | "true" => { evaluator.exit_code = 0; true }
        "false" => { evaluator.exit_code = 1; true }
        "command" => { handle_command(evaluator, args); true }
        "read" => { handle_read(evaluator, args); true }
        "exec" => { handle_exec(args); true }
        "eval" => { handle_eval(evaluator, args); true }
        "trap" => { rush_core::trap::handle_trap(args); true }
        "umask" => { handle_umask(args); true }
        "hash" => { true } // no-op: we don't cache command locations
        "readonly" => { handle_readonly(evaluator, args); true }
        "shift" => { handle_shift(evaluator, args); true }
        "getopts" => { handle_getopts(evaluator, args); true }
        "ulimit" => { handle_ulimit(args); true }
        "times" => { handle_times(); true }
        "fc" => { handle_fc(evaluator, args); true }
        "jobs" => { JOB_TABLE.with(|jt| jt.borrow().list()); true }
        "fg" => {
            let code = JOB_TABLE.with(|jt| {
                jt.borrow_mut().foreground(if args.is_empty() { None } else { Some(args) })
            });
            evaluator.exit_code = code.unwrap_or(1);
            true
        }
        "bg" => {
            JOB_TABLE.with(|jt| {
                jt.borrow_mut().background(if args.is_empty() { None } else { Some(args) });
            });
            true
        }
        "wait" => {
            let code = JOB_TABLE.with(|jt| {
                jt.borrow_mut().wait(if args.is_empty() { None } else { Some(args) })
            });
            evaluator.exit_code = code;
            true
        }
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

    // POSIX set flags
    if rush_core::flags::handle_set_flag(args) {
        return;
    }

    match args {
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

// ── setbg ───────────────────────────────────────────────────────────

fn handle_setbg(args: &str) {
    if args.is_empty() || args == "reset" {
        // Reset to default
        unsafe { std::env::remove_var("RUSH_BG") };
        // Restore terminal default background
        print!("\x1b]111\x07"); // OSC 111: reset bg
        use std::io::Write;
        std::io::stdout().flush().ok();
        println!("Background reset.");
        return;
    }

    let save = args.contains("--save");
    let local = args.contains("--local");
    let hex = args.split_whitespace()
        .find(|w| w.starts_with('#') || w.chars().all(|c| c.is_ascii_hexdigit()))
        .unwrap_or(args.split_whitespace().next().unwrap_or(""));

    let hex = if hex.starts_with('#') { hex.to_string() } else { format!("#{hex}") };

    match rush_core::theme::set_background(&hex, true) {
        Some(theme) => {
            // Re-set color env vars with new theme
            rush_core::theme::set_native_color_env_vars(&theme);
            println!("Background set to {hex} ({})", if theme.is_dark { "dark" } else { "light" });

            if save {
                let mut config = rush_core::config::RushConfig::load();
                config.set("bg", &hex);
                config.save().ok();
            }
            if local {
                std::fs::write(".rushbg", &hex).ok();
            }
        }
        None => eprintln!("setbg: invalid hex color '{hex}'"),
    }
}

// ── which / type ────────────────────────────────────────────────────

fn handle_which(name: &str) {
    if name.is_empty() {
        eprintln!("which: usage: which <command>");
        return;
    }

    // Check builtins
    let builtins = [
        "cd", "export", "unset", "source", "clear", "exit", "quit", "pwd",
        "alias", "unalias", "pushd", "popd", "dirs", "history", "path",
        "help", "set", "setbg", "which", "type",
    ];
    if builtins.contains(&name) {
        println!("{name}: shell builtin");
        return;
    }

    // Check aliases
    ALIASES.with(|a| {
        if let Some(exp) = a.borrow().get(name) {
            println!("{name}: aliased to '{exp}'");
        }
    });

    // Check PATH
    if let Some(path) = rush_core::process::which(name) {
        println!("{path}");
    } else {
        eprintln!("{name}: not found");
    }
}

// ── command — bypass functions/aliases, run external ────────────────

fn handle_command(evaluator: &mut Evaluator, args: &str) {
    if args.is_empty() {
        eprintln!("command: usage: command [-v|-V] name [args]");
        return;
    }

    // command -v name → like which (POSIX)
    if args.starts_with("-v ") || args.starts_with("-V ") {
        let name = args[3..].trim();
        handle_which(name);
        return;
    }

    // command name args → run as external, skip functions/aliases
    let result = rush_core::process::run_native(args);
    evaluator.exit_code = result.exit_code;
    if !result.stderr.is_empty() {
        eprintln!("{}", result.stderr);
    }
}

// ── read — read line from stdin into variable ───────────────────────

fn handle_read(evaluator: &mut Evaluator, args: &str) {
    let mut prompt = None;
    let mut var_name = "REPLY".to_string();

    let parts: Vec<&str> = args.split_whitespace().collect();
    let mut i = 0;
    while i < parts.len() {
        match parts[i] {
            "-p" if i + 1 < parts.len() => {
                prompt = Some(parts[i + 1]);
                i += 2;
            }
            name => {
                var_name = name.to_string();
                i += 1;
            }
        }
    }

    if let Some(p) = prompt {
        use std::io::Write;
        print!("{p} ");
        std::io::stdout().flush().ok();
    }

    let mut line = String::new();
    match std::io::stdin().read_line(&mut line) {
        Ok(0) => {
            evaluator.exit_code = 1; // EOF
        }
        Ok(_) => {
            let value = line.trim_end_matches('\n').trim_end_matches('\r');
            evaluator.env.set(&var_name, rush_core::value::Value::String(value.to_string()));
            evaluator.exit_code = 0;
        }
        Err(_) => {
            evaluator.exit_code = 1;
        }
    }
}

// ── exec — replace process ──────────────────────────────────────────

fn handle_exec(args: &str) {
    if args.is_empty() {
        return; // exec with no args = apply redirections (not implemented)
    }
    let parts = rush_core::process::parse_command_line(args);
    if parts.is_empty() {
        return;
    }

    // On Unix, exec replaces the process
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        let mut cmd = std::process::Command::new(&parts[0]);
        for arg in &parts[1..] {
            cmd.arg(arg);
        }
        let err = cmd.exec(); // does not return on success
        eprintln!("exec: {}: {err}", parts[0]);
        std::process::exit(127);
    }

    #[cfg(not(unix))]
    {
        eprintln!("exec: not supported on this platform");
    }
}

// ── eval — concatenate and execute ──────────────────────────────────

fn handle_eval(evaluator: &mut Evaluator, args: &str) {
    if args.is_empty() {
        evaluator.exit_code = 0;
        return;
    }
    // Route through the same dispatch as any other command
    crate::run_line(evaluator, args);
}

// ── umask ───────────────────────────────────────────────────────────

fn handle_umask(args: &str) {
    #[cfg(unix)]
    {
        if args.is_empty() {
            // Display current umask
            let current = unsafe { libc::umask(0) };
            unsafe { libc::umask(current); } // restore
            println!("{:04o}", current);
        } else {
            // Set umask
            if let Ok(mask) = u32::from_str_radix(args.trim(), 8) {
                unsafe { libc::umask(mask as libc::mode_t); }
            } else {
                eprintln!("umask: invalid octal number '{args}'");
            }
        }
    }
    #[cfg(not(unix))]
    {
        eprintln!("umask: not supported on this platform");
    }
}

// ── readonly ────────────────────────────────────────────────────────

fn handle_readonly(evaluator: &mut Evaluator, args: &str) {
    if args.is_empty() || args == "-p" {
        // List readonly variables (we don't track this yet — stub)
        println!("(no readonly variables)");
        return;
    }
    // readonly VAR=value or readonly VAR
    // For now, just set the variable (tracking immutability requires env changes)
    if let Some((name, value)) = args.split_once('=') {
        let value = value.trim().trim_matches('"').trim_matches('\'');
        evaluator.env.set(name.trim(), rush_core::value::Value::String(value.to_string()));
        // TODO: mark as readonly in env
    }
    eprintln!("readonly: immutability tracking not yet implemented");
}

// ── fc — history editing ─────────────────────────────────────────────

fn handle_fc(evaluator: &mut Evaluator, args: &str) {
    let history_path = config_dir().join("history");
    let content = std::fs::read_to_string(&history_path).unwrap_or_default();
    let lines: Vec<&str> = content.lines().collect();

    if args.is_empty() || args == "-l" {
        // List last 16 history entries
        let start = lines.len().saturating_sub(16);
        for (i, line) in lines[start..].iter().enumerate() {
            println!("{:5}  {line}", start + i + 1);
        }
        return;
    }

    if args.starts_with("-l ") {
        // fc -l first [last]
        let parts: Vec<&str> = args[3..].split_whitespace().collect();
        let first: usize = parts.first().and_then(|s| s.parse().ok()).unwrap_or(lines.len().saturating_sub(16));
        let last: usize = parts.get(1).and_then(|s| s.parse().ok()).unwrap_or(lines.len());
        let first = first.saturating_sub(1).min(lines.len());
        let last = last.min(lines.len());
        for (i, line) in lines[first..last].iter().enumerate() {
            println!("{:5}  {line}", first + i + 1);
        }
        return;
    }

    if args == "-e -" || args.starts_with("-s") {
        // fc -s [pat=rep] [cmd] — re-execute last command
        if let Some(last) = lines.last() {
            println!("{last}");
            crate::run_line(evaluator, last);
        }
        return;
    }

    eprintln!("fc: usage: fc [-l] [first [last]] | fc -s [cmd]");
}

// ── shift — shift positional parameters ──────────────────────────────

fn handle_shift(evaluator: &mut Evaluator, args: &str) {
    let n: usize = args.trim().parse().unwrap_or(1);
    if let Some(rush_core::value::Value::Array(ref argv)) = evaluator.env.get("ARGV").cloned() {
        let new_argv: Vec<rush_core::value::Value> = argv.iter().skip(n).cloned().collect();
        evaluator.env.set("ARGV", rush_core::value::Value::Array(new_argv));
        evaluator.exit_code = 0;
    } else {
        evaluator.exit_code = 1;
    }
}

// ── getopts — option parsing for scripts ────────────────────────────

fn handle_getopts(evaluator: &mut Evaluator, args: &str) {
    // getopts optstring name [arg...]
    // Simplified: parse next option from ARGV
    let parts: Vec<&str> = args.split_whitespace().collect();
    if parts.len() < 2 {
        eprintln!("getopts: usage: getopts optstring name [args...]");
        evaluator.exit_code = 1;
        return;
    }

    let optstring = parts[0];
    let name = parts[1];

    // Get OPTIND (1-based index into args)
    let optind: usize = evaluator.env.get("OPTIND")
        .and_then(|v| v.to_int())
        .unwrap_or(1) as usize;

    // Get args (from ARGV or remaining parts)
    let argv: Vec<String> = if parts.len() > 2 {
        parts[2..].iter().map(|s| s.to_string()).collect()
    } else {
        evaluator.env.get("ARGV")
            .and_then(|v| if let rush_core::value::Value::Array(arr) = v {
                Some(arr.iter().map(|v| v.to_rush_string()).collect())
            } else { None })
            .unwrap_or_default()
    };

    if optind > argv.len() {
        evaluator.exit_code = 1;
        return;
    }

    let arg = &argv[optind - 1];
    if !arg.starts_with('-') || arg == "-" || arg == "--" {
        evaluator.exit_code = 1;
        return;
    }

    let opt = &arg[1..2]; // first option char
    evaluator.env.set(name, rush_core::value::Value::String(opt.to_string()));

    // Check if option takes an argument
    if let Some(pos) = optstring.find(opt) {
        if optstring.get(pos + 1..pos + 2) == Some(":") {
            // Option takes argument
            if arg.len() > 2 {
                evaluator.env.set("OPTARG", rush_core::value::Value::String(arg[2..].to_string()));
            } else if optind < argv.len() {
                evaluator.env.set("OPTARG", rush_core::value::Value::String(argv[optind].clone()));
                evaluator.env.set("OPTIND", rush_core::value::Value::Int(optind as i64 + 2));
                evaluator.exit_code = 0;
                return;
            }
        }
    }

    evaluator.env.set("OPTIND", rush_core::value::Value::Int(optind as i64 + 1));
    evaluator.exit_code = 0;
}

// ── ulimit — resource limits ────────────────────────────────────────

fn handle_ulimit(args: &str) {
    #[cfg(unix)]
    {
        if args.is_empty() || args == "-f" {
            // Default: show file size limit
            let mut rlim: libc::rlimit = unsafe { std::mem::zeroed() };
            unsafe { libc::getrlimit(libc::RLIMIT_FSIZE, &mut rlim); }
            if rlim.rlim_cur == libc::RLIM_INFINITY {
                println!("unlimited");
            } else {
                println!("{}", rlim.rlim_cur / 512); // in 512-byte blocks
            }
        } else if args == "-a" {
            // Show all limits
            let limits = [
                ("core file size", libc::RLIMIT_CORE),
                ("data seg size", libc::RLIMIT_DATA),
                ("file size", libc::RLIMIT_FSIZE),
                ("max memory size", libc::RLIMIT_RSS),
                ("open files", libc::RLIMIT_NOFILE),
                ("stack size", libc::RLIMIT_STACK),
                ("cpu time", libc::RLIMIT_CPU),
            ];
            for (name, resource) in &limits {
                let mut rlim: libc::rlimit = unsafe { std::mem::zeroed() };
                unsafe { libc::getrlimit(*resource, &mut rlim); }
                let val = if rlim.rlim_cur == libc::RLIM_INFINITY {
                    "unlimited".to_string()
                } else {
                    rlim.rlim_cur.to_string()
                };
                println!("{name:20} {val}");
            }
        } else if args == "-n" {
            let mut rlim: libc::rlimit = unsafe { std::mem::zeroed() };
            unsafe { libc::getrlimit(libc::RLIMIT_NOFILE, &mut rlim); }
            println!("{}", rlim.rlim_cur);
        } else {
            eprintln!("ulimit: usage: ulimit [-a|-f|-n] [limit]");
        }
    }
    #[cfg(not(unix))]
    {
        eprintln!("ulimit: not supported on this platform");
    }
}

// ── times — process times ───────────────────────────────────────────

fn handle_times() {
    #[cfg(unix)]
    {
        let mut tms: libc::tms = unsafe { std::mem::zeroed() };
        unsafe { libc::times(&mut tms); }
        let ticks = unsafe { libc::sysconf(libc::_SC_CLK_TCK) } as f64;
        println!("{}m{:.3}s {}m{:.3}s",
            (tms.tms_utime as f64 / ticks / 60.0) as u64,
            (tms.tms_utime as f64 / ticks) % 60.0,
            (tms.tms_stime as f64 / ticks / 60.0) as u64,
            (tms.tms_stime as f64 / ticks) % 60.0,
        );
        println!("{}m{:.3}s {}m{:.3}s",
            (tms.tms_cutime as f64 / ticks / 60.0) as u64,
            (tms.tms_cutime as f64 / ticks) % 60.0,
            (tms.tms_cstime as f64 / ticks / 60.0) as u64,
            (tms.tms_cstime as f64 / ticks) % 60.0,
        );
    }
    #[cfg(not(unix))]
    {
        println!("0m0.000s 0m0.000s");
        println!("0m0.000s 0m0.000s");
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
