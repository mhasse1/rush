using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rush;

/// <summary>
/// Installs rush as an MCP server into Claude Code and Claude Desktop.
/// Follows the same pattern as engram's `e2 install` command.
///
/// Updates three files:
///   ~/.claude/mcp.json                                — Claude Code MCP servers
///   ~/Library/Application Support/Claude/claude_desktop_config.json — Claude Desktop
///   ~/.claude/settings.json                           — Claude Code permissions
/// </summary>
public static class McpInstaller
{
    private const string ServerName = "rush";

    private static readonly string[] AllowedTools =
    {
        "mcp__rush__rush_execute",
        "mcp__rush__rush_read_file",
        "mcp__rush__rush_context",
    };

    public static void InstallClaude(string version)
    {
        Console.WriteLine($"Installing Rush MCP server v{version} into Claude...");
        Console.WriteLine();

        // Resolve the full path to the rush binary (follows symlinks)
        var rushPath = GetRushBinaryPath();

        // 1. Claude Code — ~/.claude/mcp.json
        var claudeCodePath = GetClaudeCodeConfigPath();
        UpdateMcpConfig(claudeCodePath, "rush", rushPath, "Claude Code");

        // 2. Claude Desktop — platform-specific config path
        var desktopPath = GetClaudeDesktopConfigPath();
        if (desktopPath != null)
            UpdateMcpConfig(desktopPath, rushPath, rushPath, "Claude Desktop");
        else
            Console.WriteLine("   - Claude Desktop config not found (skipped)");

        // 3. Claude Code settings — permissions allow list
        var settingsPath = GetClaudeCodeSettingsPath();
        UpdateClaudeSettings(settingsPath);

        Console.WriteLine();
        Console.WriteLine("Done! Rush MCP server installed.");
        Console.WriteLine($"   Binary: {rushPath}");
        Console.WriteLine($"   Server: {ServerName}");
        Console.WriteLine();
        Console.WriteLine("Restart Claude Code / Claude Desktop to pick up the new server.");
    }

    // ── Config file locations ──────────────────────────────────────────

    private static string GetRushBinaryPath()
    {
        // Environment.ProcessPath gives the actual binary path.
        // Resolve symlinks so Claude Desktop gets the real location.
        var procPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine rush binary path");

        // Follow symlink chain to the real binary
        var info = new FileInfo(procPath);
        if (info.LinkTarget != null)
        {
            var resolved = Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(procPath)!);
            if (File.Exists(resolved))
                return resolved;
        }
        return Path.GetFullPath(procPath);
    }

    private static string GetClaudeCodeConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeDir = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claudeDir);
        return Path.Combine(claudeDir, "mcp.json");
    }

    private static string? GetClaudeDesktopConfigPath()
    {
        string path;
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        }
        else if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            path = Path.Combine(appData, "Claude", "claude_desktop_config.json");
        }
        else
        {
            // Linux: ~/.config/Claude/claude_desktop_config.json
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, ".config", "Claude", "claude_desktop_config.json");
        }

        return File.Exists(path) ? path : null;
    }

    private static string GetClaudeCodeSettingsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeDir = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claudeDir);
        return Path.Combine(claudeDir, "settings.json");
    }

    // ── JSON helper ─────────────────────────────────────────────────────

    /// <summary>
    /// Write a JsonNode as pretty-printed JSON string.
    /// Uses JsonWriterOptions directly to avoid the .NET 8 TypeInfoResolver issue
    /// that occurs when passing JsonSerializerOptions to ToJsonString().
    /// </summary>
    private static string WritePrettyJson(JsonNode root)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── Config file updaters ───────────────────────────────────────────

    private static void UpdateMcpConfig(string configPath, string command, string rushPath, string label)
    {
        // Read existing config or start fresh
        JsonNode root;
        if (File.Exists(configPath))
        {
            var content = File.ReadAllText(configPath);
            root = JsonNode.Parse(content) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        // Ensure mcpServers object exists
        if (root["mcpServers"] == null)
            root.AsObject().Add("mcpServers", new JsonObject());

        // Set the rush server entry.
        // For Claude Code: command = "rush" (in PATH), for Desktop: full path.
        root["mcpServers"]!.AsObject().Remove(ServerName);
        root["mcpServers"]!.AsObject().Add(ServerName, new JsonObject
        {
            ["command"] = command,
            ["args"] = new JsonArray { "--mcp" }
        });

        // Write back with indentation
        File.WriteAllText(configPath, WritePrettyJson(root));

        Console.WriteLine($"   + {label}: {configPath}");
    }

    private static void UpdateClaudeSettings(string settingsPath)
    {
        // Read existing settings or start fresh
        JsonNode root;
        if (File.Exists(settingsPath))
        {
            var content = File.ReadAllText(settingsPath);
            root = JsonNode.Parse(content) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        // Ensure permissions.allow array exists
        if (root["permissions"] == null)
            root.AsObject().Add("permissions", new JsonObject());
        if (root["permissions"]!["allow"] == null)
            root["permissions"]!.AsObject().Add("allow", new JsonArray());

        var allowList = root["permissions"]!["allow"]!.AsArray();

        // Collect existing entries as strings for dedup
        var existing = new HashSet<string>();
        foreach (var item in allowList)
        {
            if (item != null)
                existing.Add(item.GetValue<string>());
        }

        // Add each tool if not already present
        int added = 0;
        foreach (var tool in AllowedTools)
        {
            if (!existing.Contains(tool))
            {
                allowList.Add(tool);
                added++;
            }
        }

        // Write back with indentation
        File.WriteAllText(settingsPath, WritePrettyJson(root));

        if (added > 0)
            Console.WriteLine($"   + Added {added} rush tools to permissions: {settingsPath}");
        else
            Console.WriteLine($"   + Rush tools already permitted: {settingsPath}");
    }
}
