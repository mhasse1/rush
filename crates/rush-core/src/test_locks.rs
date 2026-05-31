//! Cross-module mutexes for serializing tests that mutate process-global
//! state (env vars, libc::umask, cwd, shell flags). Tests acquire the
//! relevant lock at the top of the body and hold it for the duration —
//! the guard's `Drop` releases on test exit, including panic unwinding.
//!
//! Use `unwrap_or_else(|e| e.into_inner())` on `lock()` so a panicked
//! poisoning test doesn't cascade-fail every later test in the suite.
//! See #239 / #223.

use std::sync::Mutex;

/// Covers any test that reads or writes process-global env vars that
/// other tests in the workspace touch — HOME, USER, USERPROFILE, PATH,
/// CDPATH, RUSH_OS, RUSH_OS_VERSION, RUSH_BG, RUSH_FLAVOR, RUSH_ACCENT,
/// and anything else that shares scope across modules.
///
/// One lock for the lot rather than per-var: simpler, no risk of
/// deadlock from a test that touches two vars, and the contention cost
/// in the cargo-test runner is negligible compared to the actual test
/// work. Per-feature locks like UMASK_LOCK (libc::umask) and
/// THEME_ENV_LOCK (RUSH_FLAVOR/RUSH_ACCENT specifically, kept for the
/// theme-table tests that hold it across multiple operations) stay
/// where they are.
pub(crate) static ENV_LOCK: Mutex<()> = Mutex::new(());

/// Covers tests that toggle shell flags (`set -e`, `set -f`, etc.).
/// Flag bits in flags.rs are process-wide atomics — concurrent toggles
/// race even without env-var involvement.
pub(crate) static FLAGS_LOCK: Mutex<()> = Mutex::new(());
