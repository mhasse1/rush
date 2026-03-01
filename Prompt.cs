using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Rush;

/// <summary>
/// Renders the shell prompt with cwd, git branch, and last exit status.
/// </summary>
public class Prompt
{
    private bool _lastCommandFailed;

    public void SetLastCommandFailed(bool failed)
    {
        _lastCommandFailed = failed;
    }

    /// <summary>
    /// Render the prompt to the console. Does not include a newline.
    /// Format: ~/path (branch) >
    /// </summary>
    public void Render(Runspace runspace)
    {
        var cwd = GetCwd(runspace);
        var branch = GetGitBranch(cwd);

        // CWD
        Console.ForegroundColor = Theme.Current.PromptPath;
        Console.Write(ShortenPath(cwd));

        // Git branch
        if (branch != null)
        {
            Console.ForegroundColor = Theme.Current.PromptGitBranch;
            Console.Write($" ({branch})");
        }

        // Prompt symbol — red if last command failed
        Console.ForegroundColor = _lastCommandFailed ? Theme.Current.PromptFailed : Theme.Current.PromptSuccess;
        Console.Write(" > ");
        Console.ResetColor();
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

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home))
            return "~" + path[home.Length..];
        return path;
    }

    private static string? GetGitBranch(string cwd)
    {
        // Expand ~ back to full path for git
        if (cwd.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            cwd = home + cwd[1..];
        }

        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var branch = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);

            if (proc.ExitCode != 0 || string.IsNullOrEmpty(branch))
                return null;

            return branch;
        }
        catch
        {
            return null;
        }
    }
}
