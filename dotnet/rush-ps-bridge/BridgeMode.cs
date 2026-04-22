// --bridge mode — plugin JSON-lines protocol.
//
// Wire shape (mirrors what crates/rush-core/src/plugin.rs expects):
//
//   server → client (startup):    {"ready": true, "plugin": "ps-bridge", "version": "…"}
//   client → server (per script): "<script source as a JSON string>"
//   server → client (per result): {"status":"success"|"error",
//                                  "stdout":"…","stderr":"…","exit_code":N}
//                                 {"ready": true}           ← context line
//
// One line per JSON document. Rush's plugin dispatcher reads the
// result line, then the ready line, then sends the next script.

using System.Management.Automation;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Rush.PsBridge;

internal static class BridgeMode
{
    public static int Run(PsRunner runner)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        // Startup ready line.
        WriteJsonLine(new { ready = true, plugin = "ps-bridge", version });

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Each incoming line is a JSON-quoted string containing the
            // script source. Rush's dispatcher sends
            // `serde_json::to_string(&command)` which wraps + escapes.
            string script;
            try
            {
                script = JsonSerializer.Deserialize<string>(line) ?? "";
            }
            catch (JsonException ex)
            {
                WriteJsonLine(new
                {
                    status = "error",
                    stdout = "",
                    stderr = $"rush-ps-bridge: malformed request: {ex.Message}",
                    exit_code = 2,
                });
                WriteJsonLine(new { ready = true });
                continue;
            }

            var result = runner.Invoke(script);
            var (stdout, stderr) = Render(result);
            var status = result.HadErrors ? "error" : "success";
            var exitCode = result.HadErrors ? 1 : 0;

            WriteJsonLine(new { status, stdout, stderr, exit_code = exitCode });
            WriteJsonLine(new { ready = true });
        }

        return 0;
    }

    /// <summary>
    /// Flatten the separated PS streams to plain stdout/stderr text.
    /// Success stream uses Out-String-style rendering so complex
    /// objects render like `Get-Service` would in a real console
    /// (property columns, not just ToString()). Error / Warning
    /// streams concatenate their Message fields.
    /// </summary>
    private static (string Stdout, string Stderr) Render(PsResult result)
    {
        var stdout = new StringBuilder();
        foreach (var obj in result.Success)
        {
            if (obj == null) continue;
            stdout.AppendLine(obj.ToString());
        }

        var stderr = new StringBuilder();
        foreach (var err in result.Errors)
        {
            stderr.AppendLine(err.Exception?.Message ?? err.ToString());
        }
        // Warnings go to stderr too — consistent with how most tools
        // surface them; the plugin protocol has no separate channel.
        foreach (var w in result.Warnings)
        {
            stderr.AppendLine($"WARNING: {w.Message}");
        }

        return (stdout.ToString().TrimEnd('\r', '\n'), stderr.ToString().TrimEnd('\r', '\n'));
    }

    private static void WriteJsonLine(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        Console.Out.WriteLine(json);
        Console.Out.Flush();
    }
}
