using System.Text.Json;

namespace Rush;

/// <summary>
/// Rush configuration loaded from ~/.config/rush/config.json
/// </summary>
public class RushConfig
{
    public string EditMode { get; set; } = "vi";
    public Dictionary<string, string> Aliases { get; set; } = new();
    public string PromptFormat { get; set; } = "default";

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    /// <summary>
    /// Load config from disk. Returns defaults if no config file exists.
    /// Creates a default config file on first run.
    /// </summary>
    public static RushConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<RushConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return config ?? new RushConfig();
            }
        }
        catch
        {
            // Corrupt config — use defaults
        }

        // Create default config on first run
        var defaultConfig = new RushConfig();
        defaultConfig.Save();
        return defaultConfig;
    }

    /// <summary>
    /// Save current config to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Can't write config — silently continue
        }
    }

    /// <summary>
    /// Apply config settings to the shell components.
    /// </summary>
    public void Apply(LineEditor editor, CommandTranslator translator)
    {
        // Edit mode
        editor.Mode = EditMode.Equals("emacs", StringComparison.OrdinalIgnoreCase)
            ? Rush.EditMode.Emacs
            : Rush.EditMode.Vi;

        // Custom aliases
        foreach (var (alias, command) in Aliases)
        {
            translator.RegisterAlias(alias, command);
        }
    }

    public static string GetConfigPath() => ConfigPath;
}
