using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rush;

/// <summary>
/// Configuration model for ~/.config/rush/databases.json
/// Stores named database connections and default settings.
/// </summary>
public class SqlConnectionConfig
{
    [JsonPropertyName("connections")]
    public Dictionary<string, ConnectionEntry> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("defaults")]
    public SqlDefaults Defaults { get; set; } = new();

    private static readonly string ConfigPath = Path.Combine(
        RushConfig.GetConfigDir(), "databases.json");

    public static SqlConnectionConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new SqlConnectionConfig();

            var raw = File.ReadAllText(ConfigPath);
            var json = RushConfig.StripJsonComments(raw);
            return JsonSerializer.Deserialize<SqlConnectionConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new SqlConnectionConfig();
        }
        catch
        {
            return new SqlConnectionConfig();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        File.WriteAllText(ConfigPath, json);
    }

    public ConnectionEntry? GetConnection(string name)
    {
        // Strip leading @ if present
        if (name.StartsWith('@'))
            name = name[1..];
        return Connections.TryGetValue(name, out var entry) ? entry : null;
    }

    public void SetConnection(string name, ConnectionEntry entry)
    {
        if (name.StartsWith('@'))
            name = name[1..];
        Connections[name] = entry;
    }

    public bool RemoveConnection(string name)
    {
        if (name.StartsWith('@'))
            name = name[1..];
        return Connections.Remove(name);
    }
}

public class ConnectionEntry
{
    [JsonPropertyName("driver")]
    public string Driver { get; set; } = "";

    // SQLite
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    // Server-based (Postgres, etc.)
    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Port { get; set; }

    [JsonPropertyName("database")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Database { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    /// <summary>
    /// Name of environment variable containing the password (set in secrets.rush).
    /// Never store passwords directly in databases.json.
    /// </summary>
    [JsonPropertyName("passwordEnvVar")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordEnvVar { get; set; }

    // ODBC
    [JsonPropertyName("dsn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Dsn { get; set; }

    // Raw connection string (any driver)
    [JsonPropertyName("connectionString")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionString { get; set; }
}

public class SqlDefaults
{
    [JsonPropertyName("rowLimit")]
    public int RowLimit { get; set; } = 1000;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("nullDisplay")]
    public string NullDisplay { get; set; } = "NULL";
}
