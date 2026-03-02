using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;

namespace Rush;

/// <summary>
/// Multi-line prompt with rich context. Default layout:
///
///   (blank line)
///   [ROOT]                                    ← only if privileged, forced/non-overridable
///   ✓ 14:32  mark@macbook  rush/src  main*    ← info line (customizable via rush_prompt function)
///     {cursor}                                ← 2-space input prefix, clean for copy-paste
///
/// Continuation prompt: 4 spaces ("    ").
/// Customization: define a rush_prompt() function in config.rush to replace the info line.
/// </summary>
public class Prompt
{
    private bool _lastCommandFailed;
    private int _lastExitCode;

    /// <summary>Continuation prompt string — 4 spaces for clean multi-line input.</summary>
    public const string Continuation = "    ";

    /// <summary>Input line prefix — 2 spaces for clean copy-paste.</summary>
    public const string InputPrefix = "  ";

    public void SetLastCommandFailed(bool failed, int exitCode = 1)
    {
        _lastCommandFailed = failed;
        _lastExitCode = failed ? exitCode : 0;
    }

    /// <summary>
    /// Render the full multi-line prompt to the console.
    /// </summary>
    public void Render(Runspace runspace)
    {
        // ── Blank line separator ──────────────────────────────────────
        Console.WriteLine();

        // ── [ROOT] indicator (forced, non-overridable) ────────────────
        if (IsRoot())
        {
            Console.ForegroundColor = Theme.Current.PromptRoot;
            Console.Write("[ROOT]");
            Console.ResetColor();
            Console.WriteLine();
        }

        // ── Info line ─────────────────────────────────────────────────
        // Try custom rush_prompt function first; fall back to default
        if (!TryRenderCustomPrompt(runspace))
        {
            RenderDefaultInfoLine(runspace);
        }
        Console.WriteLine();

        // ── Input line (just 2 spaces) ────────────────────────────────
        Console.Write(InputPrefix);
    }

    /// <summary>
    /// Render just the input prefix (used after tab completion redraw, etc.).
    /// Does NOT include the info line or blank line separator.
    /// </summary>
    public void RenderInputPrefix()
    {
        Console.Write(InputPrefix);
    }

    // ── Default Info Line ─────────────────────────────────────────────

    private void RenderDefaultInfoLine(Runspace runspace)
    {
        var cwd = GetCwd(runspace);

        // Exit status: ✓ or ✗ (with exit code only when > 1, since 1 is generic)
        if (_lastCommandFailed)
        {
            Console.ForegroundColor = Theme.Current.PromptFailed;
            Console.Write(_lastExitCode > 1 ? $"✗ {_lastExitCode}" : "✗");
        }
        else
        {
            Console.ForegroundColor = Theme.Current.PromptSuccess;
            Console.Write("✓");
        }

        // Time (24h HH:mm)
        Console.ForegroundColor = Theme.Current.PromptTime;
        Console.Write($" {DateTime.Now:HH:mm}");

        // user@host (highlight hostname differently when SSH)
        var user = Environment.UserName;
        var host = GetShortHostname();
        bool isSsh = IsSshSession();

        Console.Write("  ");
        Console.ForegroundColor = Theme.Current.PromptUser;
        Console.Write(user);
        Console.ForegroundColor = Theme.Current.Muted;
        Console.Write("@");

        if (isSsh)
        {
            Console.ForegroundColor = Theme.Current.PromptSshHost;
            Console.Write(host);
        }
        else
        {
            Console.ForegroundColor = Theme.Current.PromptHost;
            Console.Write(host);
        }

        // CWD (2 levels max)
        Console.Write("  ");
        Console.ForegroundColor = Theme.Current.PromptPath;
        Console.Write(ShortenPath(cwd));

        // Git branch + dirty state
        var (branch, isDirty) = GetGitBranchAndDirty(cwd);
        if (branch != null)
        {
            Console.Write("  ");
            Console.ForegroundColor = Theme.Current.PromptGitBranch;
            Console.Write(branch);
            if (isDirty)
            {
                Console.ForegroundColor = Theme.Current.PromptGitDirty;
                Console.Write("*");
            }
        }

        Console.ResetColor();
    }

    // ── Custom Prompt Function ────────────────────────────────────────

    /// <summary>
    /// If a rush_prompt function exists in the runspace, invoke it and write its output.
    /// The function receives $exit_code, $exit_failed, $is_ssh, $is_root as PS variables.
    /// Returns true if the custom function was found and ran.
    /// </summary>
    private bool TryRenderCustomPrompt(Runspace runspace)
    {
        try
        {
            // Check if rush_prompt function exists
            using var check = PowerShell.Create();
            check.Runspace = runspace;
            check.AddScript("Get-Command rush_prompt -ErrorAction SilentlyContinue");
            var exists = check.Invoke();
            if (exists.Count == 0) return false;

            // Set context variables the prompt function can use
            using var setup = PowerShell.Create();
            setup.Runspace = runspace;
            setup.AddScript(
                $"$exit_code = {_lastExitCode}; " +
                $"$exit_failed = ${(_lastCommandFailed ? "true" : "false")}; " +
                $"$is_ssh = ${(IsSshSession() ? "true" : "false")}; " +
                $"$is_root = ${(IsRoot() ? "true" : "false")}");
            setup.Invoke();

            // Invoke the custom prompt function
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript("rush_prompt");
            var results = ps.Invoke();

            // Write its output (the function should use Write-Host or return strings)
            foreach (var obj in results)
            {
                if (obj != null)
                    Console.Write(obj.ToString());
            }

            return true;
        }
        catch
        {
            // Custom prompt failed — fall back to default
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static bool IsRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: check if running elevated
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
        else
        {
            // Unix: uid 0 = root
            return geteuid() == 0;
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static bool IsSshSession()
    {
        // Check standard SSH environment variables
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT"));
    }

    private static string GetShortHostname()
    {
        var name = Environment.MachineName;
        // Strip domain suffix for cleaner display (e.g., "macbook.local" → "macbook")
        var dot = name.IndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.ToLowerInvariant();
    }

    private static string GetCwd(Runspace runspace)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Get-Location");
            var loc = ps.Invoke();
            return loc.Count > 0 ? loc[0].ToString()! : "~";
        }
        catch
        {
            return "~";
        }
    }

    /// <summary>
    /// Shorten the path: replace home with ~, then show only last 2 directory levels.
    /// /Users/mark/src/rush/bin/Debug → ~/rush/bin/Debug  → bin/Debug
    /// Wait — the 2-level rule keeps context. Let's do:
    ///   ~/src/rush/bin/Debug → rush/bin (parent + current only, drop the prefix)
    ///   ~ → ~
    ///   ~/src → ~/src  (already ≤ 2 levels from ~)
    /// </summary>
    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home))
            path = "~" + path[home.Length..];

        // Split and take last 2 components (ignoring ~ prefix)
        var sep = Path.DirectorySeparatorChar;
        var parts = path.Split(sep, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length <= 2)
            return path; // Already short enough: "~", "~/src", "/etc"

        // If starts with ~, it's home-relative — show last 2 dirs
        // e.g., ~/src/rush/bin → rush/bin
        // If absolute path outside home — show last 2 dirs
        // e.g., /usr/local/bin → local/bin
        return parts[^2] + sep + parts[^1];
    }

    /// <summary>
    /// Get git branch name and dirty state in a single efficient call.
    /// </summary>
    private static (string? Branch, bool IsDirty) GetGitBranchAndDirty(string cwd)
    {
        // Expand ~ back to full path for git
        if (cwd.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            cwd = home + cwd[1..];
        }

        // Also handle the shortened 2-level paths by using the actual CWD
        // The shortened path might not be a valid filesystem path, so we
        // need to resolve it. But since we get cwd from Get-Location, the
        // full path is available before shortening. Let's restructure:
        // Actually the cwd parameter here is the FULL path from GetCwd().
        // ShortenPath is only called in RenderDefaultInfoLine for display.
        // So cwd here is already the full path. Good.

        string? branch = null;
        bool isDirty = false;

        try
        {
            // Get branch name
            var psi = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (null, false);

            branch = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);

            if (proc.ExitCode != 0 || string.IsNullOrEmpty(branch))
                return (null, false);
        }
        catch
        {
            return (null, false);
        }

        try
        {
            // Check dirty state: git status --porcelain is empty = clean
            var psi2 = new ProcessStartInfo("git", "status --porcelain")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc2 = Process.Start(psi2);
            if (proc2 != null)
            {
                var output = proc2.StandardOutput.ReadToEnd();
                proc2.WaitForExit(1000);
                isDirty = !string.IsNullOrEmpty(output.Trim());
            }
        }
        catch
        {
            // If we can't check dirty state, just show the branch without *
        }

        return (branch, isDirty);
    }
}
