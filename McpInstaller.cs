using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rush;

/// <summary>
/// Installs rush MCP servers (rush-local + rush-ssh) into Claude Code and Claude Desktop.
/// Follows the same pattern as engram's `e2 install` command.
///
/// Updates three files:
///   ~/.claude/mcp.json                                — Claude Code MCP servers
///   ~/Library/Application Support/Claude/claude_desktop_config.json — Claude Desktop
///   ~/.claude/settings.json                           — Claude Code permissions
/// </summary>
public static class McpInstaller
{
    private const string LocalServerName = "rush-local";
    private const string SshServerName = "rush-ssh";

    private static readonly string[] AllowedTools =
    {
        // rush-local tools
        "mcp__rush-local__rush_execute",
        "mcp__rush-local__rush_read_file",
        "mcp__rush-local__rush_context",
        // rush-ssh tools
        "mcp__rush-ssh__rush_execute",
        "mcp__rush-ssh__rush_read_file",
        "mcp__rush-ssh__rush_context",
    };

    public static void InstallClaude(string version)
    {
        Console.WriteLine($"Installing Rush MCP servers v{version} into Claude...");
        Console.WriteLine();

        var rushPath = GetRushBinaryPath();

        // 1. Claude Code — ~/.claude/mcp.json
        var claudeCodePath = GetClaudeCodeConfigPath();
        UpdateMcpConfig(claudeCodePath, "rush", "Claude Code");

        // 2. Claude Desktop — platform-specific config path
        var desktopPath = GetClaudeDesktopConfigPath();
        if (desktopPath != null)
            UpdateMcpConfig(desktopPath, rushPath, "Claude Desktop");
        else
            Console.WriteLine("   - Claude Desktop config not found (skipped)");

        // 3. Claude Code settings — permissions allow list
        var settingsPath = GetClaudeCodeSettingsPath();
        UpdateClaudeSettings(settingsPath);

        Console.WriteLine();
        Console.WriteLine("Done! Rush MCP servers installed.");
        Console.WriteLine($"   Binary:  {rushPath}");
        Console.WriteLine($"   Servers: {LocalServerName} (persistent local), {SshServerName} (SSH gateway)");
        Console.WriteLine();
        Console.WriteLine("Restart Claude Code / Claude Desktop to pick up the new servers.");
    }

    // ── Config file locations ──────────────────────────────────────────

    private static string GetRushBinaryPath()
    {
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

    /// <summary>
    /// Register both rush-local and rush-ssh in an MCP config file.
    /// Cleans up the old "rush" entry from previous installs.
    /// </summary>
    private static void UpdateMcpConfig(string configPath, string command, string label)
    {
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

        if (root["mcpServers"] == null)
            root.AsObject().Add("mcpServers", new JsonObject());

        var servers = root["mcpServers"]!.AsObject();

        // Clean up old "rush" entry from previous install (migration)
        servers.Remove("rush");

        // Register rush-local: rush --mcp
        servers.Remove(LocalServerName);
        servers.Add(LocalServerName, new JsonObject
        {
            ["command"] = (JsonNode)command,
            ["args"] = new JsonArray { "--mcp" }
        });

        // Register rush-ssh: rush --mcp-ssh
        servers.Remove(SshServerName);
        servers.Add(SshServerName, new JsonObject
        {
            ["command"] = (JsonNode)command,
            ["args"] = new JsonArray { "--mcp-ssh" }
        });

        File.WriteAllText(configPath, WritePrettyJson(root));
        Console.WriteLine($"   + {label}: {configPath}");
    }

    private static void UpdateClaudeSettings(string settingsPath)
    {
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

        if (root["permissions"] == null)
            root.AsObject().Add("permissions", new JsonObject());
        if (root["permissions"]!["allow"] == null)
            root["permissions"]!.AsObject().Add("allow", new JsonArray());

        var allowList = root["permissions"]!["allow"]!.AsArray();

        // Collect existing entries for dedup
        var existing = new HashSet<string>();
        foreach (var item in allowList)
        {
            if (item != null)
                existing.Add(item.GetValue<string>());
        }

        // Clean up old "rush" permissions (migration)
        var oldPermissions = new[] { "mcp__rush__rush_execute", "mcp__rush__rush_read_file", "mcp__rush__rush_context" };
        for (int i = allowList.Count - 1; i >= 0; i--)
        {
            var val = allowList[i]?.GetValue<string>();
            if (val != null && oldPermissions.Contains(val))
            {
                allowList.RemoveAt(i);
                existing.Remove(val);
            }
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

        File.WriteAllText(settingsPath, WritePrettyJson(root));

        if (added > 0)
            Console.WriteLine($"   + Added {added} rush tools to permissions: {settingsPath}");
        else
            Console.WriteLine($"   + Rush tools already permitted: {settingsPath}");
    }
}
