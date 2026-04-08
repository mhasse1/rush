//! Job control: background process tracking, fg/bg/jobs/wait.
//!
//! Each pipeline gets a job entry. Background jobs (&) run in their own
//! process group with SIGINT/SIGQUIT set to SIG_IGN (per POSIX).

use std::collections::HashMap;
use crate::platform::{self, Sig, WaitResult};

/// Job state.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum JobState {
    Running,
    Stopped,
    Done(i32),      // exit code
}

/// A tracked job.
#[derive(Debug, Clone)]
pub struct Job {
    pub id: usize,
    pub pid: u32,
    pub pgid: u32,
    pub command: String,
    pub state: JobState,
    pub background: bool,
}

/// Job table — tracks all background and stopped jobs.
#[derive(Debug)]
pub struct JobTable {
    jobs: HashMap<usize, Job>,
    next_id: usize,
}

impl JobTable {
    pub fn new() -> Self {
        Self {
            jobs: HashMap::new(),
            next_id: 1,
        }
    }

    /// Add a new background job. Returns the job ID.
    pub fn add(&mut self, pid: u32, pgid: u32, command: &str) -> usize {
        let id = self.next_id;
        self.next_id += 1;
        self.jobs.insert(id, Job {
            id,
            pid,
            pgid,
            command: command.to_string(),
            state: JobState::Running,
            background: true,
        });
        eprintln!("[{id}] {pid}");
        id
    }

    /// Check for completed/stopped jobs (non-blocking).
    /// Returns list of jobs that changed state.
    pub fn reap(&mut self) -> Vec<(usize, String, JobState)> {
        let mut changed = Vec::new();
        let p = platform::current();

        for job in self.jobs.values_mut() {
            if job.state != JobState::Running {
                continue;
            }

            if let Some(result) = p.try_wait_pid(job.pid) {
                match result {
                    WaitResult::Exited(code) => {
                        job.state = JobState::Done(code);
                    }
                    WaitResult::Signaled(sig) => {
                        job.state = JobState::Done(128 + sig);
                    }
                    WaitResult::Stopped(_) => {
                        job.state = JobState::Stopped;
                    }
                }
                changed.push((job.id, job.command.clone(), job.state));
            }
        }

        changed
    }

    /// Report and remove completed jobs. Call before each prompt.
    pub fn report_done(&mut self) {
        let changed = self.reap();
        for (id, cmd, state) in &changed {
            match state {
                JobState::Done(code) => {
                    if *code == 0 {
                        eprintln!("[{id}]  Done                    {cmd}");
                    } else {
                        eprintln!("[{id}]  Exit {code}               {cmd}");
                    }
                }
                JobState::Stopped => {
                    eprintln!("[{id}]  Stopped                 {cmd}");
                }
                _ => {}
            }
        }
        // Remove done jobs
        self.jobs.retain(|_, j| j.state != JobState::Done(0) && !matches!(j.state, JobState::Done(_)));
    }

    /// List all jobs.
    pub fn list(&self) {
        let mut ids: Vec<usize> = self.jobs.keys().copied().collect();
        ids.sort();
        for id in ids {
            let job = &self.jobs[&id];
            let state_str = match job.state {
                JobState::Running => "Running",
                JobState::Stopped => "Stopped",
                JobState::Done(0) => "Done",
                JobState::Done(_) => "Exit",
            };
            let marker = if id == self.current_job_id() { "+" } else { "-" };
            println!("[{id}]{marker}  {state_str:24}{}", job.command);
        }
    }

    /// Get the current (most recent) job ID.
    fn current_job_id(&self) -> usize {
        self.jobs.keys().max().copied().unwrap_or(0)
    }

    /// Bring a job to the foreground.
    pub fn foreground(&mut self, job_spec: Option<&str>) -> Option<i32> {
        let id = self.resolve_job_spec(job_spec)?;
        let job = self.jobs.get_mut(&id)?;
        let p = platform::current();

        eprintln!("{}", job.command);

        // Resume if stopped
        if job.state == JobState::Stopped {
            p.kill_pg(job.pgid, Sig::Cont);
            job.state = JobState::Running;
        }

        // Give terminal to job's process group
        p.set_foreground_pgid(job.pgid);

        // Wait for it
        let result = p.wait_pid(job.pid);

        // Reclaim terminal
        p.reclaim_terminal();

        match result {
            WaitResult::Exited(code) => {
                self.jobs.remove(&id);
                Some(code)
            }
            WaitResult::Stopped(_) => {
                let job = self.jobs.get_mut(&id).unwrap();
                job.state = JobState::Stopped;
                eprintln!("\n[{id}]+  Stopped                 {}", job.command);
                Some(result.exit_code())
            }
            WaitResult::Signaled(sig) => {
                self.jobs.remove(&id);
                Some(128 + sig)
            }
        }
    }

    /// Resume a job in the background.
    pub fn background(&mut self, job_spec: Option<&str>) -> bool {
        let id = match self.resolve_job_spec(job_spec) {
            Some(id) => id,
            None => {
                eprintln!("bg: no current job");
                return false;
            }
        };

        let job = match self.jobs.get_mut(&id) {
            Some(j) => j,
            None => {
                eprintln!("bg: no such job");
                return false;
            }
        };

        if job.state != JobState::Stopped {
            eprintln!("bg: job {} is not stopped", id);
            return false;
        }

        let p = platform::current();
        p.kill_pg(job.pgid, Sig::Cont);

        job.state = JobState::Running;
        job.background = true;
        eprintln!("[{id}]+ {} &", job.command);
        true
    }

    /// Wait for a specific job or all jobs.
    pub fn wait(&mut self, job_spec: Option<&str>) -> i32 {
        if let Some(spec) = job_spec {
            if let Some(id) = self.resolve_job_spec(Some(spec)) {
                return self.wait_for_job(id);
            }
            eprintln!("wait: no such job");
            return 127;
        }

        // Wait for all background jobs
        let ids: Vec<usize> = self.jobs.keys().copied().collect();
        let mut last_code = 0;
        for id in ids {
            last_code = self.wait_for_job(id);
        }
        last_code
    }

    fn wait_for_job(&mut self, id: usize) -> i32 {
        let job = match self.jobs.get(&id) {
            Some(j) => j,
            None => return 127,
        };

        let p = platform::current();
        let result = p.wait_pid(job.pid);
        let code = result.exit_code();
        self.jobs.remove(&id);
        code
    }

    /// Resolve a job spec (%N, %%, or None for current).
    fn resolve_job_spec(&self, spec: Option<&str>) -> Option<usize> {
        match spec {
            None | Some("%%") | Some("%+") => {
                // Current job (most recent)
                self.jobs.keys().max().copied()
            }
            Some(s) if s.starts_with('%') => {
                let rest = &s[1..];
                if let Ok(n) = rest.parse::<usize>() {
                    if self.jobs.contains_key(&n) { Some(n) } else { None }
                } else {
                    // %string — find job whose command starts with string
                    self.jobs.values()
                        .find(|j| j.command.starts_with(rest))
                        .map(|j| j.id)
                }
            }
            Some(s) => {
                if let Ok(n) = s.parse::<usize>() {
                    if self.jobs.contains_key(&n) { Some(n) } else { None }
                } else {
                    None
                }
            }
        }
    }

    /// Send SIGHUP to all jobs on shell exit (POSIX requirement).
    pub fn shutdown(&mut self) {
        let p = platform::current();
        for job in self.jobs.values() {
            if job.state == JobState::Stopped {
                p.kill_pg(job.pgid, Sig::Cont);
            }
            p.kill_pg(job.pgid, Sig::Hup);
        }
    }

    pub fn is_empty(&self) -> bool {
        self.jobs.is_empty()
    }
}

impl Default for JobTable {
    fn default() -> Self { Self::new() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn job_table_add() {
        let mut table = JobTable::new();
        let id = table.add(1234, 1234, "sleep 60");
        assert_eq!(id, 1);
        assert!(!table.is_empty());
    }

    #[test]
    fn job_table_list() {
        let mut table = JobTable::new();
        table.add(1234, 1234, "sleep 60");
        table.add(5678, 5678, "make -j4");
        table.list(); // just verify no crash
    }

    #[test]
    fn resolve_job_spec() {
        let mut table = JobTable::new();
        table.add(1234, 1234, "sleep 60");
        table.add(5678, 5678, "make -j4");

        assert_eq!(table.resolve_job_spec(Some("%1")), Some(1));
        assert_eq!(table.resolve_job_spec(Some("%2")), Some(2));
        assert_eq!(table.resolve_job_spec(None), Some(2)); // current = most recent
        assert_eq!(table.resolve_job_spec(Some("%%")), Some(2));
        assert_eq!(table.resolve_job_spec(Some("%sleep")), Some(1)); // by prefix
        assert_eq!(table.resolve_job_spec(Some("%99")), None);
    }
}
