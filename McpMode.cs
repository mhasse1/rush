using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rush;

/// <summary>
/// MCP (Model Context Protocol) server mode — JSON-RPC 2.0 over stdio.
/// Exposes rush_execute, rush_read_file, rush_context as persistent tools.
/// State (variables, cwd, env) survives across tool calls.
///
/// Hand-rolled protocol: no NuGet dependency. The MCP protocol is trivial —
/// just three RPC methods (initialize, tools/list, tools/call).
///
/// Usage:
///   rush --mcp                              # local MCP server
///   ssh trinity "rush --mcp"                # remote MCP via SSH stdio pipe
///   claude mcp add rush-local -- rush --mcp # register with Claude Code
/// </summary>
public class McpMode
{
    private readonly LlmMode _llm;
    private readonly string _version;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public McpMode(Runspace runspace, ScriptEngine scriptEngine, CommandTranslator translator, string version)
    {
        _llm = new LlmMode(runspace, scriptEngine, translator, version);
        _version = version;
    }

    public void Run()
    {
        // Same environment hardening as LlmMode
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
        Environment.SetEnvironmentVariable("CI", "true");
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
        Environment.SetEnvironmentVariable("DEBIAN_FRONTEND", "noninteractive");

        // MCP requires all logging go to stderr — stdout is the JSON-RPC transport
        Console.Error.WriteLine($"[rush-mcp] Rush MCP server v{_version} starting");

        while (true)
        {
            var line = Console.ReadLine();
            if (line == null) break; // EOF — client disconnected

            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try
            {
                msg = JsonNode.Parse(line);
            }
            catch
            {
                WriteError(null, -32700, "Parse error");
                continue;
            }

            if (msg == null)
            {
                WriteError(null, -32700, "Parse error");
                continue;
            }

            var id = msg["id"];
            var method = msg["method"]?.GetValue<string>();

            // Notifications (no id) — MCP sends notifications/initialized after init
            if (id == null)
            {
                // Swallow notifications silently (e.g. notifications/initialized)
                continue;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        HandleInitialize(id);
                        break;
                    case "tools/list":
                        HandleToolsList(id);
                        break;
                    case "tools/call":
                        HandleToolsCall(id, msg["params"]);
                        break;
                    default:
                        WriteError(id, -32601, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[rush-mcp] Error handling {method}: {ex.Message}");
                WriteError(id, -32603, $"Internal error: {ex.Message}");
            }
        }

        Console.Error.WriteLine("[rush-mcp] Server shutting down (EOF)");
    }

    // ── initialize ─────────────────────────────────────────────────────

    private void HandleInitialize(JsonNode id)
    {
        var result = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "rush",
                ["version"] = _version
            }
        };

        WriteResult(id, result);
    }

    // ── tools/list ─────────────────────────────────────────────────────

    private void HandleToolsList(JsonNode id)
    {
        var tools = new JsonArray
        {
            MakeTool(
                "rush_execute",
                "Execute a command in the persistent Rush shell session. Supports Rush syntax (Ruby-like), Unix shell commands, and PowerShell. Variables, cwd, and environment persist across calls.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The command to execute (Rush syntax, shell command, or PowerShell)"
                        }
                    },
                    ["required"] = new JsonArray { "command" }
                }),

            MakeTool(
                "rush_read_file",
                "Read a file and return its content. Text files return UTF-8 content; binary files return base64. Includes MIME type and size metadata.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Path to the file to read (absolute or relative to cwd)"
                        }
                    },
                    ["required"] = new JsonArray { "path" }
                }),

            MakeTool(
                "rush_context",
                "Get current shell context: hostname, cwd, git branch/dirty status, last exit code.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                })
        };

        var result = new JsonObject
        {
            ["tools"] = tools
        };

        WriteResult(id, result);
    }

    // ── tools/call ─────────────────────────────────────────────────────

    private void HandleToolsCall(JsonNode id, JsonNode? parameters)
    {
        var toolName = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"];

        if (string.IsNullOrEmpty(toolName))
        {
            WriteError(id, -32602, "Missing tool name");
            return;
        }

        string resultJson;
        bool isError = false;

        switch (toolName)
        {
            case "rush_execute":
            {
                var command = arguments?["command"]?.GetValue<string>();
                if (string.IsNullOrEmpty(command))
                {
                    WriteError(id, -32602, "Missing required parameter: command");
                    return;
                }
                var llmResult = _llm.ExecuteCommand(command);
                isError = llmResult.Status != "success" && llmResult.Status != "output_limit";
                resultJson = JsonSerializer.Serialize(llmResult, JsonOpts);
                break;
            }
            case "rush_read_file":
            {
                var path = arguments?["path"]?.GetValue<string>();
                if (string.IsNullOrEmpty(path))
                {
                    WriteError(id, -32602, "Missing required parameter: path");
                    return;
                }
                var llmResult = LlmFileReader.ReadFile(path);
                isError = llmResult.Status != "success";
                resultJson = JsonSerializer.Serialize(llmResult, JsonOpts);
                break;
            }
            case "rush_context":
            {
                var ctx = _llm.GetContext();
                resultJson = JsonSerializer.Serialize(ctx, JsonOpts);
                break;
            }
            default:
                WriteError(id, -32602, $"Unknown tool: {toolName}");
                return;
        }

        // MCP tools/call result wraps tool output in content array
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = resultJson
            }
        };

        var result = new JsonObject
        {
            ["content"] = content,
            ["isError"] = isError
        };

        WriteResult(id, result);
    }

    // ── JSON-RPC helpers ───────────────────────────────────────────────

    private static JsonObject MakeTool(string name, string description, JsonObject inputSchema)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema
        };
    }

    private static void WriteResult(JsonNode id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result
        };
        Console.WriteLine(response.ToJsonString());
    }

    private static void WriteError(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        Console.WriteLine(response.ToJsonString());
    }
}
