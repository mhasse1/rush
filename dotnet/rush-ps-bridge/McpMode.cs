// --mcp mode — MCP JSON-RPC 2.0 server on stdio.
//
// Exposes PowerShell as an MCP tool so any MCP client (rush's mcp()
// builtin, Claude Desktop, Claude Code, etc.) can drive it. One tool
// today — `invoke(script)` — runs a PS script and returns the output
// as text content. More tools (reset_session, invoke_structured, …)
// land in later phases if use cases ask for them.
//
// Protocol: MCP 2024-11-05. Same subset implemented by rush's own
// MCP server in crates/rush-core/src/mcp.rs — initialize, tools/list,
// tools/call, notifications/initialized, and error responses per
// JSON-RPC 2.0.

using System.Management.Automation;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rush.PsBridge;

internal static class McpMode
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "rush-ps-bridge";

    private const string Instructions =
        "Runs PowerShell scripts in a persistent runspace. Use the `invoke` "
        + "tool with a `script` argument. Variables, function definitions, "
        + "and imported modules persist across calls within a session. "
        + "Script output is returned as text (what the script would have "
        + "written to the console). Errors are surfaced via isError=true.";

    public static int Run(PsRunner runner)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        Console.Error.WriteLine($"[rush-ps-bridge-mcp] starting, version {version}");

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            JsonNode? msg;
            try
            {
                msg = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                WriteError(JsonValue.Create<int?>(null), -32700, "Parse error");
                continue;
            }
            if (msg == null)
            {
                WriteError(JsonValue.Create<int?>(null), -32700, "Parse error");
                continue;
            }

            var id = msg["id"];
            var method = msg["method"]?.GetValue<string>() ?? "";

            // Notifications (no id) — no response expected. Just consume.
            if (id == null) continue;

            try
            {
                switch (method)
                {
                    case "initialize":
                        WriteResult(id, HandleInitialize(version));
                        break;
                    case "tools/list":
                        WriteResult(id, HandleToolsList());
                        break;
                    case "tools/call":
                        WriteResult(id, HandleToolsCall(runner, msg["params"]));
                        break;
                    case "ping":
                        WriteResult(id, new JsonObject());
                        break;
                    case "shutdown":
                        WriteResult(id, new JsonObject());
                        return 0;
                    default:
                        WriteError(id, -32601, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteError(id, -32603, $"Internal error: {ex.Message}");
            }
        }

        return 0;
    }

    // ── handlers ───────────────────────────────────────────────────

    private static JsonNode HandleInitialize(string version) => new JsonObject
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = ServerName,
            ["version"] = version,
        },
        ["instructions"] = Instructions,
    };

    private static JsonNode HandleToolsList() => new JsonObject
    {
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "invoke",
                ["description"] =
                    "Run a PowerShell script in the persistent runspace and return its output. "
                    + "Variables and function definitions persist across calls. Errors are "
                    + "signaled via isError=true in the result.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["script"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "PowerShell script source to execute.",
                        },
                    },
                    ["required"] = new JsonArray { "script" },
                },
            },
        },
    };

    private static JsonNode HandleToolsCall(PsRunner runner, JsonNode? paramsNode)
    {
        if (paramsNode == null)
        {
            return ErrorContent("missing params");
        }
        var name = paramsNode["name"]?.GetValue<string>() ?? "";
        if (name != "invoke")
        {
            return ErrorContent($"unknown tool: {name}");
        }

        var args = paramsNode["arguments"];
        var script = args?["script"]?.GetValue<string>();
        if (script == null)
        {
            return ErrorContent("missing required argument: script");
        }

        var result = runner.Invoke(script);
        var text = RenderForMcp(result);

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["isError"] = result.HadErrors,
        };
    }

    // ── rendering ──────────────────────────────────────────────────

    /// <summary>
    /// Format a PS invocation result as a single block of text for the
    /// MCP `content[0].text` field. Success stream lines come first;
    /// errors (if any) follow with an "ERROR:" prefix; warnings with
    /// "WARNING:". Verbose / Information dropped — if consumers want
    /// them, we can add a separate tool or extend `content`.
    /// </summary>
    private static string RenderForMcp(PsResult result)
    {
        var sb = new StringBuilder();
        foreach (var obj in result.Success)
        {
            if (obj == null) continue;
            sb.AppendLine(obj.ToString());
        }
        foreach (var w in result.Warnings)
        {
            sb.AppendLine($"WARNING: {w.Message}");
        }
        foreach (var err in result.Errors)
        {
            sb.AppendLine($"ERROR: {err.Exception?.Message ?? err.ToString()}");
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    // ── wire write helpers ────────────────────────────────────────

    private static void WriteResult(JsonNode idNode, JsonNode result)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode.DeepClone(),
            ["result"] = result,
        };
        Console.Out.WriteLine(msg.ToJsonString());
        Console.Out.Flush();
    }

    private static void WriteError(JsonNode? idNode, int code, string message)
    {
        var idClone = idNode?.DeepClone() ?? JsonValue.Create<object?>(null);
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idClone,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        Console.Out.WriteLine(msg.ToJsonString());
        Console.Out.Flush();
    }

    private static JsonNode ErrorContent(string message) => new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = message,
            },
        },
        ["isError"] = true,
    };
}
