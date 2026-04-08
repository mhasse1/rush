//! Config sync: push/pull config files across machines.
//! Transports: github (via gh+git), ssh (via scp), path (filesystem).

use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// Files tracked by sync (relative to config dir).
const SYNC_FILES: &[&str] = &["config.json", "init.rush"];

/// Sync metadata stored in ~/.config/rush/sync.json
#[derive(Debug, Serialize, Deserialize)]
pub struct SyncMeta {
    pub initialized: bool,
    pub transport: String,
    pub target: String,
    pub last_push: Option<String>,
    pub last_pull: Option<String>,
}

impl Default for SyncMeta {
    fn default() -> Self {
        Self {
            initialized: false,
            transport: String::new(),
            target: String::new(),
            last_push: None,
            last_pull: None,
        }
    }
}

fn config_dir() -> PathBuf {
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_else(|_| ".".into());
    PathBuf::from(home).join(".config").join("rush")
}

fn meta_path() -> PathBuf {
    config_dir().join("sync.json")
}

fn load_meta() -> SyncMeta {
    if let Ok(content) = std::fs::read_to_string(meta_path()) {
        serde_json::from_str(&content).unwrap_or_default()
    } else {
        SyncMeta::default()
    }
}

fn save_meta(meta: &SyncMeta) {
    if let Ok(json) = serde_json::to_string_pretty(meta) {
        std::fs::write(meta_path(), json).ok();
    }
}

fn timestamp() -> String {
    crate::platform::current().local_time_hhmm().replace(':', "") +
        &format!("{}", std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs()).unwrap_or(0))
}

/// Handle the `sync` command.
pub fn handle_sync(args: &str) -> bool {
    let parts: Vec<&str> = args.split_whitespace().collect();
    let subcmd = parts.first().copied().unwrap_or("status");
    let rest = parts.get(1..).unwrap_or(&[]).join(" ");
    let force = rest.contains("--force");
    let rest = rest.replace("--force", "").trim().to_string();

    match subcmd {
        "init" => handle_init(&rest),
        "push" => handle_push(force),
        "pull" => handle_pull(force),
        "status" => handle_status(),
        _ => {
            eprintln!("sync: unknown command '{subcmd}'");
            eprintln!("  Usage: sync init [github|ssh|path] [target]");
            eprintln!("         sync push [--force] | sync pull [--force] | sync status");
            false
        }
    }
}

// ── init ────────────────────────────────────────────────────────────

fn handle_init(args: &str) -> bool {
    let parts: Vec<&str> = args.split_whitespace().collect();
    let transport = parts.first().copied().unwrap_or("");
    let target = parts.get(1..).unwrap_or(&[]).join(" ");

    if transport.is_empty() {
        eprintln!("  Sync transports:");
        eprintln!("    github — Private GitHub repo (requires gh + git)");
        eprintln!("    ssh    — Remote server via SCP (requires SSH keys)");
        eprintln!("    path   — Filesystem path (share, USB, cloud drive)");
        eprintln!("  Usage: sync init github [repo-name]");
        eprintln!("         sync init ssh user@host:/path");
        eprintln!("         sync init path /path/to/sync/dir");
        return false;
    }

    let target = match transport {
        "github" => {
            if target.is_empty() { "rush-config".to_string() } else { target }
        }
        "ssh" => {
            if target.is_empty() {
                eprintln!("sync: ssh requires target: sync init ssh user@host:path");
                return false;
            }
            target
        }
        "path" => {
            if target.is_empty() {
                eprintln!("sync: path requires target: sync init path /path/to/dir");
                return false;
            }
            target
        }
        _ => {
            eprintln!("sync: unknown transport '{transport}'. Use: github, ssh, or path");
            return false;
        }
    };

    // Initialize transport
    let ok = match transport {
        "github" => init_github(&target),
        "ssh" => init_ssh(&target),
        "path" => init_path(&target),
        _ => false,
    };

    if ok {
        let meta = SyncMeta {
            initialized: true,
            transport: transport.to_string(),
            target: target.clone(),
            last_push: None,
            last_pull: None,
        };
        save_meta(&meta);
        println!("  Sync initialized: {transport} → {target}");
        println!("  Run 'sync push' to upload current config.");
    }

    ok
}

// ── push ────────────────────────────────────────────────────────────

fn handle_push(force: bool) -> bool {
    let meta = load_meta();
    if !meta.initialized {
        eprintln!("sync: not initialized. Run 'sync init' first.");
        return false;
    }

    let ok = match meta.transport.as_str() {
        "github" => push_github(&meta.target, force),
        "ssh" => push_ssh(&meta.target),
        "path" => push_path(&meta.target),
        _ => { eprintln!("sync: unknown transport"); false }
    };

    if ok {
        let mut meta2 = meta;
        meta2.last_push = Some(timestamp());
        save_meta(&meta2);
        println!("  Config pushed.");
    }

    ok
}

// ── pull ────────────────────────────────────────────────────────────

fn handle_pull(force: bool) -> bool {
    let meta = load_meta();
    if !meta.initialized {
        eprintln!("sync: not initialized. Run 'sync init' first.");
        return false;
    }

    let ok = match meta.transport.as_str() {
        "github" => pull_github(&meta.target, force),
        "ssh" => pull_ssh(&meta.target),
        "path" => pull_path(&meta.target),
        _ => { eprintln!("sync: unknown transport"); false }
    };

    if ok {
        let mut meta2 = meta;
        meta2.last_pull = Some(timestamp());
        save_meta(&meta2);
        println!("  Config pulled. Run 'reload' to apply.");
    }

    ok
}

// ── status ──────────────────────────────────────────────────────────

fn handle_status() -> bool {
    let meta = load_meta();
    if !meta.initialized {
        println!("  Not synced. Run 'sync init' to set up config sync.");
        return true;
    }

    println!("  Transport: {}", meta.transport);
    println!("  Target:    {}", meta.target);
    if let Some(ref t) = meta.last_push { println!("  Last push: {t}"); }
    if let Some(ref t) = meta.last_pull { println!("  Last pull: {t}"); }

    // Show tracked files
    let cd = config_dir();
    for file in SYNC_FILES {
        let path = cd.join(file);
        let status = if path.exists() { "✓" } else { "✗" };
        println!("  {status} {file}");
    }

    true
}

// ── GitHub transport ────────────────────────────────────────────────

fn init_github(repo_name: &str) -> bool {
    // Check gh is available
    if std::process::Command::new("gh").arg("--version").output().is_err() {
        eprintln!("sync: 'gh' CLI not found. Install: brew install gh");
        return false;
    }

    // Check if repo exists, create if not
    let check = std::process::Command::new("gh")
        .args(["repo", "view", repo_name])
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status();

    if check.map(|s| !s.success()).unwrap_or(true) {
        println!("  Creating private repo '{repo_name}'...");
        let create = std::process::Command::new("gh")
            .args(["repo", "create", repo_name, "--private", "--confirm"])
            .status();
        if create.map(|s| !s.success()).unwrap_or(true) {
            eprintln!("sync: failed to create repo");
            return false;
        }
    }

    // Clone into config dir as .sync-repo
    let sync_dir = config_dir().join(".sync-repo");
    if !sync_dir.exists() {
        let clone = std::process::Command::new("gh")
            .args(["repo", "clone", repo_name, &sync_dir.to_string_lossy()])
            .status();
        if clone.map(|s| !s.success()).unwrap_or(true) {
            eprintln!("sync: failed to clone repo");
            return false;
        }
    }

    true
}

fn push_github(_repo_name: &str, _force: bool) -> bool {
    let sync_dir = config_dir().join(".sync-repo");
    let cd = config_dir();

    // Copy tracked files to sync repo
    for file in SYNC_FILES {
        let src = cd.join(file);
        let dst = sync_dir.join(file);
        if src.exists() {
            std::fs::copy(&src, &dst).ok();
        }
    }

    // Git add, commit, push
    let run = |args: &[&str]| -> bool {
        std::process::Command::new("git")
            .args(args)
            .current_dir(&sync_dir)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .map(|s| s.success())
            .unwrap_or(false)
    };

    run(&["add", "-A"]);
    run(&["commit", "-m", "sync push"]);
    run(&["push"])
}

fn pull_github(_repo_name: &str, _force: bool) -> bool {
    let sync_dir = config_dir().join(".sync-repo");
    let cd = config_dir();

    // Git pull
    let ok = std::process::Command::new("git")
        .args(["pull", "--quiet"])
        .current_dir(&sync_dir)
        .status()
        .map(|s| s.success())
        .unwrap_or(false);

    if !ok {
        eprintln!("sync: git pull failed");
        return false;
    }

    // Copy files back
    for file in SYNC_FILES {
        let src = sync_dir.join(file);
        let dst = cd.join(file);
        if src.exists() {
            std::fs::copy(&src, &dst).ok();
        }
    }

    true
}

// ── SSH transport ───────────────────────────────────────────────────

fn init_ssh(target: &str) -> bool {
    // Verify SSH connectivity
    let host = target.split(':').next().unwrap_or(target);
    let ok = std::process::Command::new("ssh")
        .args(["-o", "BatchMode=yes", "-o", "ConnectTimeout=5", host, "echo ok"])
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false);

    if !ok {
        eprintln!("sync: can't connect to {host}. Check SSH keys.");
        return false;
    }

    // Create remote directory
    let remote_path = target.split(':').nth(1).unwrap_or(".config/rush");
    std::process::Command::new("ssh")
        .args([host, &format!("mkdir -p {remote_path}")])
        .status()
        .ok();

    true
}

fn push_ssh(target: &str) -> bool {
    let cd = config_dir();
    let mut ok = true;

    for file in SYNC_FILES {
        let src = cd.join(file);
        if src.exists() {
            let dst = format!("{target}/{file}");
            let result = std::process::Command::new("scp")
                .args(["-q", &src.to_string_lossy(), &dst])
                .status();
            if result.map(|s| !s.success()).unwrap_or(true) {
                eprintln!("sync: failed to push {file}");
                ok = false;
            }
        }
    }

    ok
}

fn pull_ssh(target: &str) -> bool {
    let cd = config_dir();

    for file in SYNC_FILES {
        let src = format!("{target}/{file}");
        let dst = cd.join(file);
        let result = std::process::Command::new("scp")
            .args(["-q", &src, &dst.to_string_lossy()])
            .status();
        if result.map(|s| !s.success()).unwrap_or(true) {
            // File may not exist on remote — not an error
        }
    }

    true
}

// ── Path transport ──────────────────────────────────────────────────

fn init_path(target: &str) -> bool {
    let path = Path::new(target);
    if !path.exists() {
        std::fs::create_dir_all(path).ok();
    }
    if !path.is_dir() {
        eprintln!("sync: {target} is not a directory");
        return false;
    }
    true
}

fn push_path(target: &str) -> bool {
    let cd = config_dir();
    let dst = Path::new(target);
    let mut ok = true;

    for file in SYNC_FILES {
        let src = cd.join(file);
        if src.exists() {
            if std::fs::copy(&src, dst.join(file)).is_err() {
                eprintln!("sync: failed to copy {file}");
                ok = false;
            }
        }
    }

    ok
}

fn pull_path(target: &str) -> bool {
    let cd = config_dir();
    let src = Path::new(target);

    for file in SYNC_FILES {
        let from = src.join(file);
        if from.exists() {
            std::fs::copy(&from, cd.join(file)).ok();
        }
    }

    true
}
