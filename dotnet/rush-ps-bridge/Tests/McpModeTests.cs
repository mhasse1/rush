// Integration tests for --mcp mode.
// Drives the JSON-RPC 2.0 handshake + tools/call end-to-end.

using System.Text.Json.Nodes;
using Xunit;

namespace Rush.PsBridge.Tests;

public class McpModeTests
{
    private static JsonNode Rpc(System.Diagnostics.Process p, int id, string method, JsonNode? @params = null)
    {
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params != null) req["params"] = @params;
        p.StandardInput.WriteLine(req.ToJsonString());
        p.StandardInput.Flush();

        var line = p.StandardOutput.ReadLine()
            ?? throw new InvalidOperationException("mcp server closed stdout");
        return JsonNode.Parse(line)
            ?? throw new InvalidOperationException($"null JSON: {line}");
    }

    [Fact]
    public void Initialize_ReturnsServerInfoAndCapabilities()
    {
        using var p = TestHelper.SpawnBridge("--mcp");

        var resp = Rpc(p, 1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "test-client",
                ["version"] = "1.0",
            },
        });

        Assert.Equal(1, resp["id"]?.GetValue<int>());
        var result = resp["result"];
        Assert.NotNull(result);
        Assert.Equal("2024-11-05", result["protocolVersion"]?.GetValue<string>());
        Assert.NotNull(result["capabilities"]?["tools"]);
        Assert.Equal("rush-ps-bridge", result["serverInfo"]?["name"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(result["instructions"]?.GetValue<string>()));

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void ToolsList_ExposesInvoke()
    {
        using var p = TestHelper.SpawnBridge("--mcp");
        Rpc(p, 1, "initialize"); // must initialize before tools/list

        var resp = Rpc(p, 2, "tools/list");
        var tools = resp["result"]?["tools"]?.AsArray();
        Assert.NotNull(tools);
        Assert.Contains(tools!, t => t?["name"]?.GetValue<string>() == "invoke");

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void ToolsCall_InvokePassesScriptAndReturnsText()
    {
        using var p = TestHelper.SpawnBridge("--mcp");
        Rpc(p, 1, "initialize");

        var resp = Rpc(p, 2, "tools/call", new JsonObject
        {
            ["name"] = "invoke",
            ["arguments"] = new JsonObject
            {
                ["script"] = "'hello from ps'",
            },
        });

        var result = resp["result"];
        Assert.NotNull(result);
        Assert.False(result["isError"]?.GetValue<bool>() ?? true);
        var content = result["content"]?.AsArray();
        Assert.NotNull(content);
        Assert.Equal("text", content![0]?["type"]?.GetValue<string>());
        Assert.Equal("hello from ps", content[0]?["text"]?.GetValue<string>());

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void ToolsCall_FailingScript_ReturnsIsError()
    {
        using var p = TestHelper.SpawnBridge("--mcp");
        Rpc(p, 1, "initialize");

        var resp = Rpc(p, 2, "tools/call", new JsonObject
        {
            ["name"] = "invoke",
            ["arguments"] = new JsonObject
            {
                ["script"] = "Write-Error 'broken'",
            },
        });

        Assert.True(resp["result"]?["isError"]?.GetValue<bool>() ?? false);
        var content = resp["result"]?["content"]?.AsArray();
        Assert.NotNull(content);
        Assert.Contains("broken", content![0]?["text"]?.GetValue<string>() ?? "");

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void ToolsCall_SessionPersists()
    {
        using var p = TestHelper.SpawnBridge("--mcp");
        Rpc(p, 1, "initialize");

        Rpc(p, 2, "tools/call", new JsonObject
        {
            ["name"] = "invoke",
            ["arguments"] = new JsonObject { ["script"] = "$x = 42" },
        });

        var second = Rpc(p, 3, "tools/call", new JsonObject
        {
            ["name"] = "invoke",
            ["arguments"] = new JsonObject { ["script"] = "$x * 2" },
        });

        var text = second["result"]?["content"]?.AsArray()?[0]?["text"]?.GetValue<string>();
        Assert.Equal("84", text);

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void UnknownMethod_ReturnsMethodNotFoundError()
    {
        using var p = TestHelper.SpawnBridge("--mcp");
        Rpc(p, 1, "initialize");

        var resp = Rpc(p, 2, "totally/not/a/method");
        var err = resp["error"];
        Assert.NotNull(err);
        Assert.Equal(-32601, err["code"]?.GetValue<int>());

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    [Fact]
    public void MalformedJson_ReturnsParseError()
    {
        using var p = TestHelper.SpawnBridge("--mcp");

        p.StandardInput.WriteLine("this is not valid json");
        p.StandardInput.Flush();

        var line = p.StandardOutput.ReadLine()
            ?? throw new InvalidOperationException("mcp server closed stdout");
        var resp = JsonNode.Parse(line);
        Assert.NotNull(resp);
        Assert.Equal(-32700, resp["error"]?["code"]?.GetValue<int>());

        p.StandardInput.Close();
        p.WaitForExit(5000);
    }
}
