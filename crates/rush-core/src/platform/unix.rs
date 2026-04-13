//! Unix implementation of the Platform trait.

use super::{Platform, Sig, SigAction, TermSize, WaitResult};
use std::sync::atomic::{AtomicBool, Ordering};

static SHOULD_EXIT: AtomicBool = AtomicBool::new(false);

pub struct UnixPlatform {
    shell_pgid: u32,
}

impl UnixPlatform {
    pub fn new() -> Self {
        let pgid = unsafe { libc::getpgrp() } as u32;
        Self { shell_pgid: pgid }
    }

    fn sig_to_libc(sig: Sig) -> libc::c_int {
        match sig {
            Sig::Hup => libc::SIGHUP,
            Sig::Int => libc::SIGINT,
            Sig::Quit => libc::SIGQUIT,
            Sig::Kill => libc::SIGKILL,
            Sig::Term => libc::SIGTERM,
            Sig::Tstp => libc::SIGTSTP,
            Sig::Cont => libc::SIGCONT,
            Sig::Ttin => libc::SIGTTIN,
            Sig::Ttou => libc::SIGTTOU,
            Sig::Pipe => libc::SIGPIPE,
            Sig::Winch => libc::SIGWINCH,
        }
    }
}

impl Platform for UnixPlatform {
    fn install_signal_handlers(&self) {
        unsafe {
            libc::signal(libc::SIGHUP, handle_exit_signal as *const () as libc::sighandler_t);
            libc::signal(libc::SIGTERM, handle_exit_signal as *const () as libc::sighandler_t);
            libc::signal(libc::SIGTSTP, libc::SIG_IGN); // shell ignores Ctrl+Z
            libc::signal(libc::SIGPIPE, libc::SIG_IGN); // ignore broken pipe
            libc::signal(libc::SIGTTIN, libc::SIG_IGN); // shell ignores bg read
            libc::signal(libc::SIGTTOU, libc::SIG_IGN); // shell ignores bg write
        }
    }

    fn set_signal(&self, sig: Sig, action: SigAction) {
        let signum = Self::sig_to_libc(sig);
        let handler = match action {
            SigAction::Default => libc::SIG_DFL,
            SigAction::Ignore => libc::SIG_IGN,
        };
        unsafe { libc::signal(signum, handler); }
    }

    fn should_exit(&self) -> bool {
        SHOULD_EXIT.load(Ordering::Relaxed)
    }

    fn setup_foreground_child(&self) {
        unsafe {
            libc::setpgid(0, 0);
            libc::tcsetpgrp(libc::STDIN_FILENO, libc::getpgrp());

            // Reset signal dispositions to default (per POSIX)
            libc::signal(libc::SIGINT, libc::SIG_DFL);
            libc::signal(libc::SIGQUIT, libc::SIG_DFL);
            libc::signal(libc::SIGTSTP, libc::SIG_DFL);
            libc::signal(libc::SIGTTIN, libc::SIG_DFL);
            libc::signal(libc::SIGTTOU, libc::SIG_DFL);
            libc::signal(libc::SIGPIPE, libc::SIG_DFL);
        }
    }

    fn setup_background_child(&self) {
        unsafe {
            libc::setpgid(0, 0);
            // Background jobs ignore SIGINT/SIGQUIT (per POSIX)
            libc::signal(libc::SIGINT, libc::SIG_IGN);
            libc::signal(libc::SIGQUIT, libc::SIG_IGN);
            libc::signal(libc::SIGTSTP, libc::SIG_DFL);
            libc::signal(libc::SIGTTIN, libc::SIG_DFL);
            libc::signal(libc::SIGTTOU, libc::SIG_DFL);
            libc::signal(libc::SIGPIPE, libc::SIG_DFL);
        }
    }

    fn set_foreground_pgid(&self, pgid: u32) {
        unsafe { libc::tcsetpgrp(libc::STDIN_FILENO, pgid as libc::pid_t); }
    }

    fn reclaim_terminal(&self) {
        unsafe { libc::tcsetpgrp(libc::STDIN_FILENO, self.shell_pgid as libc::pid_t); }
    }

    fn shell_pgid(&self) -> u32 {
        self.shell_pgid
    }

    fn wait_pid(&self, pid: u32) -> WaitResult {
        let mut status: libc::c_int = 0;
        unsafe { libc::waitpid(pid as libc::pid_t, &mut status, libc::WUNTRACED); }
        decode_wait_status(status)
    }

    fn try_wait_pid(&self, pid: u32) -> Option<WaitResult> {
        let mut status: libc::c_int = 0;
        let result = unsafe {
            libc::waitpid(pid as libc::pid_t, &mut status, libc::WNOHANG | libc::WUNTRACED)
        };
        if result == pid as libc::pid_t {
            Some(decode_wait_status(status))
        } else {
            None
        }
    }

    fn kill_pg(&self, pgid: u32, sig: Sig) {
        let signum = Self::sig_to_libc(sig);
        unsafe { libc::kill(-(pgid as libc::pid_t), signum); }
    }

    fn terminal_size(&self) -> Option<TermSize> {
        unsafe {
            let mut ws: libc::winsize = std::mem::zeroed();
            if libc::ioctl(libc::STDOUT_FILENO, libc::TIOCGWINSZ, &mut ws) == 0
                && ws.ws_col > 0 && ws.ws_row > 0
            {
                Some(TermSize { cols: ws.ws_col, rows: ws.ws_row })
            } else {
                None
            }
        }
    }

    fn local_time_hhmm(&self) -> String {
        unsafe {
            let t = libc::time(std::ptr::null_mut());
            let mut tm: libc::tm = std::mem::zeroed();
            libc::localtime_r(&t, &mut tm);
            format!("{:02}:{:02}", tm.tm_hour, tm.tm_min)
        }
    }

    fn hostname(&self) -> String {
        let mut buf = [0u8; 256];
        unsafe {
            if libc::gethostname(buf.as_mut_ptr() as *mut libc::c_char, buf.len()) == 0 {
                let len = buf.iter().position(|&b| b == 0).unwrap_or(buf.len());
                let full = String::from_utf8_lossy(&buf[..len]).to_string();
                full.split('.').next().unwrap_or("unknown").to_string()
            } else {
                "unknown".to_string()
            }
        }
    }

    fn username(&self) -> String {
        std::env::var("USER").unwrap_or_else(|_| "unknown".into())
    }

    fn is_ssh(&self) -> bool {
        std::env::var("SSH_CONNECTION").is_ok()
            || std::env::var("SSH_TTY").is_ok()
            || std::env::var("SSH_CLIENT").is_ok()
    }

    fn is_root(&self) -> bool {
        unsafe { libc::geteuid() == 0 }
    }
}

fn decode_wait_status(status: libc::c_int) -> WaitResult {
    if libc::WIFEXITED(status) {
        WaitResult::Exited(libc::WEXITSTATUS(status))
    } else if libc::WIFSIGNALED(status) {
        WaitResult::Signaled(libc::WTERMSIG(status))
    } else if libc::WIFSTOPPED(status) {
        WaitResult::Stopped(libc::WSTOPSIG(status))
    } else {
        WaitResult::Exited(-1)
    }
}

extern "C" fn handle_exit_signal(_sig: libc::c_int) {
    SHOULD_EXIT.store(true, Ordering::Relaxed);
}
