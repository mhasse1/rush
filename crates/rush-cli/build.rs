// Build script: embed git-derived version info at compile time.
use std::process::Command;

fn main() {
    // Get short SHA
    let sha = Command::new("git")
        .args(["rev-parse", "--short", "HEAD"])
        .output()
        .ok()
        .and_then(|o| String::from_utf8(o.stdout).ok())
        .unwrap_or_default();
    println!("cargo:rustc-env=RUSH_GIT_SHA={}", sha.trim());

    // Get commit count for version
    let count = Command::new("git")
        .args(["rev-list", "--count", "HEAD"])
        .output()
        .ok()
        .and_then(|o| String::from_utf8(o.stdout).ok())
        .unwrap_or_default();
    println!("cargo:rustc-env=RUSH_COMMIT_COUNT={}", count.trim());

    // Dirty tree?
    let dirty = Command::new("git")
        .args(["status", "--porcelain"])
        .output()
        .ok()
        .map(|o| !o.stdout.is_empty())
        .unwrap_or(false);
    println!("cargo:rustc-env=RUSH_GIT_DIRTY={}", if dirty { "-dirty" } else { "" });

    println!("cargo:rerun-if-changed=../../.git/HEAD");
    println!("cargo:rerun-if-changed=../../.git/index");
}
