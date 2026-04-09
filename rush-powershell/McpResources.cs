using System.Reflection;
using System.Text.Json.Nodes;

namespace Rush;

/// <summary>
/// Shared MCP resource handling for rush-local and rush-ssh servers.
/// Serves the embedded rush-lang-spec.yaml so Claude knows Rush syntax.
/// </summary>
public static class McpResources
{
    public const string Instructions =
        "Rush is a Unix-style shell with clean, intent-driven syntax built on .NET. " +
        "Supports variables (x = 42), string interpolation (\"hello #{name}\"), " +
        "arrays ([1,2,3]), hashes ({a: 1}), control flow (if/unless/while/for-in), " +
        "method chaining (\"hello\".upcase), and a File/Dir/Time stdlib. " +
        "Also runs standard Unix commands (ls, grep, find, etc.). " +
        "Read the rush://lang-spec resource for the full language specification.";

    private const string SpecUri = "rush://lang-spec";
    private const string SpecName = "Rush Language Specification";
    private const string SpecMime = "text/yaml";

    // ── resources/list ─────────────────────────────────────────────────

    public static void HandleResourcesList(JsonNode id)
    {
        var resources = new JsonArray
        {
            new JsonObject
            {
                ["uri"] = SpecUri,
                ["name"] = SpecName,
                ["mimeType"] = SpecMime
            }
        };

        var result = new JsonObject
        {
            ["resources"] = resources
        };

        McpJsonRpc.WriteResult(id, result);
    }

    // ── resources/read ─────────────────────────────────────────────────

    public static void HandleResourcesRead(JsonNode id, JsonNode? parameters)
    {
        var uri = parameters?["uri"]?.GetValue<string>();

        if (uri != SpecUri)
        {
            McpJsonRpc.WriteError(id, -32602, $"Unknown resource: {uri}");
            return;
        }

        var spec = AiCommand.GetEmbeddedSpec();
        if (string.IsNullOrEmpty(spec))
        {
            McpJsonRpc.WriteError(id, -32603, "Failed to load rush-lang-spec.yaml");
            return;
        }

        var result = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = SpecUri,
                    ["mimeType"] = SpecMime,
                    ["text"] = spec
                }
            }
        };

        McpJsonRpc.WriteResult(id, result);
    }
}

/// <summary>
/// Shared JSON-RPC 2.0 helpers for MCP servers.
/// Used by both McpMode (rush-local) and McpSshMode (rush-ssh).
/// </summary>
public static class McpJsonRpc
{
    public static JsonObject MakeTool(string name, string description, JsonObject inputSchema)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema
        };
    }

    public static void WriteResult(JsonNode id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result
        };
        Console.WriteLine(response.ToJsonString());
    }

    public static void WriteError(JsonNode? id, int code, string message)
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
