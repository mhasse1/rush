// rush-ps — PowerShell execution server for Rush
// JSON wire protocol over stdio (same contract as rush --llm)
//
// Usage:
//   rush-ps              Start JSON wire protocol server
//   rush-ps --version    Show version
//
// Protocol:
//   → stdin:  JSON-quoted command string (one per line)
//   ← stdout: {ready, host, cwd, ...} context line
//   ← stdout: {status, stdout, stderr, exit_code, duration_ms} result line
//
// Install as Rush plugin:
//   Place rush-ps binary on PATH → plugin.ps blocks in Rush will find it

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RushPs;

class Program
{
    const string Version = "0.1.0";

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--version" or "-v")
        {
            Console.WriteLine($"rush-ps {Version}");
            return;
        }

        if (args.Length > 0 && args[0] is "--help" or "-h")
        {
            Console.WriteLine($"rush-ps {Version} — PowerShell execution server for Rush");
            Console.WriteLine();
            Console.WriteLine("Speaks the Rush JSON wire protocol over stdin/stdout.");
            Console.WriteLine("Place on PATH to enable plugin.ps blocks in Rush.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  rush-ps              Start wire protocol server");
            Console.WriteLine("  rush-ps --version    Show version");
            return;
        }

        // Suppress PS banner noise
        Environment.SetEnvironmentVariable("NO_COLOR", "1");

        RunServer();
    }

    static void RunServer()
    {
        // Create persistent PowerShell runspace
        var iss = InitialSessionState.CreateDefault2();
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();

        var stdin = Console.In;
        var stdout = Console.Out;
        int lastExitCode = 0;

        // Main REPL loop
        while (true)
        {
            // Emit context line
            var context = BuildContext(runspace, lastExitCode);
            stdout.WriteLine(JsonSerializer.Serialize(context, JsonCtx.Default.Context));
            stdout.Flush();

            // Read command (JSON-quoted string)
            var line = stdin.ReadLine();
            if (line == null) break; // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Parse the JSON-quoted command
            string command;
            try
            {
                command = JsonSerializer.Deserialize<string>(line) ?? line;
            }
            catch
            {
                command = line; // fallback: treat as raw string
            }

            // Execute
            var result = Execute(runspace, command);
            lastExitCode = result.ExitCode;

            // Write result
            stdout.WriteLine(JsonSerializer.Serialize(result, JsonCtx.Default.Result));
            stdout.Flush();
        }

        runspace.Close();
    }

    static Result Execute(Runspace runspace, string command)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(command);

            var output = ps.Invoke();
            sw.Stop();

            // Collect stdout
            var stdoutLines = new List<string>();
            foreach (var obj in output)
            {
                if (obj != null)
                    stdoutLines.Add(obj.ToString() ?? "");
            }

            // Collect stderr
            string? stderr = null;
            if (ps.Streams.Error.Count > 0)
            {
                var errors = ps.Streams.Error
                    .Select(e => e.ToString())
                    .ToList();
                stderr = string.Join("\n", errors);
            }

            // Determine exit code
            int exitCode = 0;
            if (ps.HadErrors)
                exitCode = 1;

            // Check $LASTEXITCODE for native commands
            try
            {
                var lastExitVar = runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
                if (lastExitVar is int lec && lec != 0)
                    exitCode = lec;
            }
            catch { }

            var stdoutStr = stdoutLines.Count > 0 ? string.Join("\n", stdoutLines) : null;
            var cwd = runspace.SessionStateProxy.Path.CurrentLocation.Path;

            return new Result
            {
                Status = exitCode == 0 ? "success" : "error",
                ExitCode = exitCode,
                Stdout = stdoutStr,
                Stderr = stderr,
                Cwd = cwd,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Result
            {
                Status = "error",
                ExitCode = 1,
                Stderr = ex.Message,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
    }

    static Context BuildContext(Runspace runspace, int lastExitCode)
    {
        var cwd = "/";
        try { cwd = runspace.SessionStateProxy.Path.CurrentLocation.Path; } catch { }

        return new Context
        {
            Ready = true,
            Host = Environment.MachineName.ToLowerInvariant(),
            User = Environment.UserName,
            Cwd = cwd,
            LastExitCode = lastExitCode,
            Shell = "powershell",
            Version = Version,
        };
    }
}

// ── JSON data objects ──────────────────────────────────────────────

class Context
{
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";
    [JsonPropertyName("last_exit_code")] public int LastExitCode { get; set; }
    [JsonPropertyName("shell")] public string Shell { get; set; } = "powershell";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

class Result
{
    [JsonPropertyName("status")] public string Status { get; set; } = "success";
    [JsonPropertyName("exit_code")] public int ExitCode { get; set; }
    [JsonPropertyName("stdout")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stdout { get; set; }
    [JsonPropertyName("stderr")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stderr { get; set; }
    [JsonPropertyName("cwd")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cwd { get; set; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
}

// ── Source-generated JSON serialization ────────────────────────────

[JsonSerializable(typeof(Context))]
[JsonSerializable(typeof(Result))]
internal partial class JsonCtx : JsonSerializerContext { }
