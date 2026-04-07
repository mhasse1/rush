using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Rush;

/// <summary>
/// Registry of built-in database drivers and connection factory.
/// Resolves named connections or inline URIs to DbConnection instances.
/// Built-in: SQLite, ODBC, PostgreSQL.
/// </summary>
public static class SqlDriverManager
{
    /// <summary>
    /// Create a DbConnection from a named connection entry.
    /// </summary>
    public static DbConnection CreateConnection(ConnectionEntry entry)
    {
        var driver = entry.Driver.ToLowerInvariant();
        var connStr = BuildConnectionString(entry);

        return driver switch
        {
            "sqlite" => new SqliteConnection(connStr),
            "postgres" or "postgresql" => new NpgsqlConnection(connStr),
            "odbc" => new OdbcConnection(connStr),
            _ => throw new NotSupportedException($"Unknown driver: {entry.Driver}. Supported: sqlite, postgres, odbc")
        };
    }

    /// <summary>
    /// Create a DbConnection from an inline URI (e.g., sqlite:///path, postgres://user:pass@host/db).
    /// </summary>
    public static DbConnection CreateConnectionFromUri(string uri)
    {
        var entry = ParseUri(uri);
        return CreateConnection(entry);
    }

    /// <summary>
    /// Parse a URI into a ConnectionEntry. Used for inline connection strings.
    /// Supported schemes: sqlite, postgres/postgresql, odbc
    /// </summary>
    public static ConnectionEntry ParseUri(string uri)
    {
        // sqlite:///path/to/db.sqlite  or  sqlite://path (relative)
        if (uri.StartsWith("sqlite://", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri["sqlite://".Length..];
            // sqlite:///absolute/path → /absolute/path (keep absolute)
            // sqlite://relative/path → relative/path
            // Expand ~
            if (path.StartsWith('~'))
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    path[2..]); // skip ~/
            return new ConnectionEntry { Driver = "sqlite", Path = path };
        }

        // postgres://user:pass@host:port/database
        if (uri.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var entry = new ConnectionEntry { Driver = "postgres" };
            var schemeEnd = uri.IndexOf("://") + 3;
            var rest = uri[schemeEnd..]; // user:pass@host:port/database

            // Parse user:pass@host:port/database
            string? userInfo = null;
            if (rest.Contains('@'))
            {
                var atIdx = rest.IndexOf('@');
                userInfo = rest[..atIdx];
                rest = rest[(atIdx + 1)..];
            }

            // Parse host:port/database
            var slashIdx = rest.IndexOf('/');
            string hostPort;
            if (slashIdx >= 0)
            {
                hostPort = rest[..slashIdx];
                entry.Database = rest[(slashIdx + 1)..];
            }
            else
            {
                hostPort = rest;
            }

            // Parse host:port
            var colonIdx = hostPort.LastIndexOf(':');
            if (colonIdx >= 0 && int.TryParse(hostPort[(colonIdx + 1)..], out var port))
            {
                entry.Host = hostPort[..colonIdx];
                entry.Port = port;
            }
            else
            {
                entry.Host = hostPort;
                entry.Port = 5432;
            }

            // Parse user:pass
            if (userInfo != null)
            {
                var passIdx = userInfo.IndexOf(':');
                if (passIdx >= 0)
                {
                    entry.User = Uri.UnescapeDataString(userInfo[..passIdx]);
                    // Store password directly in connection string (transient, not persisted)
                    entry.ConnectionString = $"Host={entry.Host};Port={entry.Port};Database={entry.Database};Username={entry.User};Password={Uri.UnescapeDataString(userInfo[(passIdx + 1)..])}";
                    return entry;
                }
                entry.User = Uri.UnescapeDataString(userInfo);
            }

            return entry;
        }

        // odbc://DSN=name  or  odbc://Driver={...};Server=...
        if (uri.StartsWith("odbc://", StringComparison.OrdinalIgnoreCase))
        {
            var connStr = uri["odbc://".Length..];
            return new ConnectionEntry { Driver = "odbc", ConnectionString = connStr };
        }

        throw new ArgumentException($"Unsupported URI scheme. Use sqlite://, postgres://, or odbc://");
    }

    /// <summary>
    /// Build an ADO.NET connection string from a ConnectionEntry.
    /// </summary>
    public static string BuildConnectionString(ConnectionEntry entry)
    {
        // If raw connection string is provided, use it directly
        if (!string.IsNullOrEmpty(entry.ConnectionString))
            return entry.ConnectionString;

        var driver = entry.Driver.ToLowerInvariant();
        return driver switch
        {
            "sqlite" => BuildSqliteConnectionString(entry),
            "postgres" or "postgresql" => BuildPostgresConnectionString(entry),
            "odbc" => BuildOdbcConnectionString(entry),
            _ => throw new NotSupportedException($"Unknown driver: {entry.Driver}")
        };
    }

    private static string BuildSqliteConnectionString(ConnectionEntry entry)
    {
        var path = entry.Path ?? ":memory:";
        // Expand ~
        if (path.StartsWith('~'))
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        return $"Data Source={path}";
    }

    private static string BuildPostgresConnectionString(ConnectionEntry entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(entry.Host))
            parts.Add($"Host={entry.Host}");
        if (entry.Port > 0)
            parts.Add($"Port={entry.Port}");
        if (!string.IsNullOrEmpty(entry.Database))
            parts.Add($"Database={entry.Database}");
        if (!string.IsNullOrEmpty(entry.User))
            parts.Add($"Username={entry.User}");

        // Resolve password from environment variable
        if (!string.IsNullOrEmpty(entry.PasswordEnvVar))
        {
            var password = Environment.GetEnvironmentVariable(entry.PasswordEnvVar);
            if (!string.IsNullOrEmpty(password))
                parts.Add($"Password={password}");
        }

        return string.Join(";", parts);
    }

    private static string BuildOdbcConnectionString(ConnectionEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Dsn))
            return $"DSN={entry.Dsn}";
        return entry.ConnectionString ?? "";
    }

    /// <summary>
    /// List of supported driver names for help/error messages.
    /// </summary>
    public static readonly string[] SupportedDrivers = { "sqlite", "postgres", "odbc" };
}
