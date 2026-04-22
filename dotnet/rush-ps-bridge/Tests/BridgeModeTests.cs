// Integration tests for --bridge mode.
// Spawns the built binary and drives the plugin JSON-lines protocol
// end-to-end. Matches the shape Rush's plugin.ps dispatcher uses.

using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Rush.PsBridge.Tests;

public class BridgeModeTests
{
    /// <summary>
    /// Helper: read one JSON line from stdout (stripping trailing newline).
    /// </summary>
    private static JsonNode ReadLine(System.Diagnostics.Process p)
    {
        var line = p.StandardOutput.ReadLine()
            ?? throw new InvalidOperationException("bridge closed stdout unexpectedly");
        var node = JsonNode.Parse(line);
        return node ?? throw new InvalidOperationException($"null JSON from bridge: {line!}");
    }

    private static void SendScript(System.Diagnostics.Process p, string script)
    {
        // The wire expects a JSON-encoded string per line.
        var encoded = JsonSerializer.Serialize(script);
        p.StandardInput.WriteLine(encoded);
        p.StandardInput.Flush();
    }

    [Fact]
    public void Startup_WritesReadyLine()
    {
        using var p = TestHelper.SpawnBridge("--bridge");
        var ready = ReadLine(p);

        Assert.True(ready["ready"]?.GetValue<bool>() == true);
        Assert.Equal("ps-bridge", ready["plugin"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(ready["version"]?.GetValue<string>()));

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void Invoke_SimpleScript_ReturnsSuccess()
    {
        using var p = TestHelper.SpawnBridge("--bridge");
        ReadLine(p); // consume ready

        SendScript(p, "Write-Output 'hello'");
        var result = ReadLine(p);
        Assert.Equal("success", result["status"]?.GetValue<string>());
        Assert.Equal("hello", result["stdout"]?.GetValue<string>());
        Assert.Equal("", result["stderr"]?.GetValue<string>());
        Assert.Equal(0, result["exit_code"]?.GetValue<int>());

        var next = ReadLine(p);
        Assert.True(next["ready"]?.GetValue<bool>() == true);

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void Invoke_FailingScript_ReturnsError()
    {
        using var p = TestHelper.SpawnBridge("--bridge");
        ReadLine(p);

        SendScript(p, "Write-Error 'broken'");
        var result = ReadLine(p);
        Assert.Equal("error", result["status"]?.GetValue<string>());
        Assert.Equal(1, result["exit_code"]?.GetValue<int>());
        Assert.Contains("broken", result["stderr"]?.GetValue<string>() ?? "");

        ReadLine(p); // consume next ready
        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void Invoke_SessionPersistsAcrossScripts()
    {
        using var p = TestHelper.SpawnBridge("--bridge");
        ReadLine(p);

        SendScript(p, "$x = 42");
        ReadLine(p); ReadLine(p); // result + ready

        SendScript(p, "$x * 2");
        var second = ReadLine(p);
        Assert.Equal("success", second["status"]?.GetValue<string>());
        Assert.Equal("84", second["stdout"]?.GetValue<string>());

        ReadLine(p);
        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void Invoke_MalformedRequest_ReportsErrorAndContinues()
    {
        using var p = TestHelper.SpawnBridge("--bridge");
        ReadLine(p);

        // Send a raw line that isn't a JSON-quoted string.
        p.StandardInput.WriteLine("this is not json");
        p.StandardInput.Flush();

        var result = ReadLine(p);
        Assert.Equal("error", result["status"]?.GetValue<string>());
        Assert.Contains("malformed", result["stderr"]?.GetValue<string>() ?? "");

        ReadLine(p); // ready

        // Next valid request should still work — the bridge kept running.
        SendScript(p, "'still alive'");
        var second = ReadLine(p);
        Assert.Equal("success", second["status"]?.GetValue<string>());
        Assert.Equal("still alive", second["stdout"]?.GetValue<string>());

        ReadLine(p);
        p.StandardInput.Close();
        p.WaitForExit(5000);
    }
}
