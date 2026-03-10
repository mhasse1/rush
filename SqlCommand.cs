using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Rush;

/// <summary>
/// Native sql command for Rush. Queries databases with human-readable table
/// or structured JSON output. Supports SQLite, PostgreSQL, and ODBC.
///
/// Usage:
///   sql @name "SELECT ..."           — named connection from databases.json
///   sql sqlite:///path "SELECT ..."   — inline URI
///   sql add @name --driver sqlite ... — manage connections
///   sql list | test | remove          — connection management
/// </summary>
public static class SqlCommand
{
    // ── Entry Point (Interactive / Non-Interactive) ─────────────────

    public static bool Execute(string argsStr)
    {
        try
        {
            return ExecuteInternal(argsStr, forLlm: false);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine($"sql: {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    // ── Entry Point (LLM Mode) ─────────────────────────────────────

    public static LlmResult ExecuteForLlm(string input, string cwd, Stopwatch sw)
    {
        try
        {
            // Strip "sql " prefix
            var argsStr = input.Length > 3 ? input[3..].TrimStart() : "";

            var (opts, remaining) = ParseOptions(argsStr);
            opts.OutputFormat = OutputFormat.Json; // force JSON in LLM mode

            var (connRef, query) = ParseConnectionAndQuery(remaining);
            if (string.IsNullOrEmpty(query))
            {
                return new LlmResult
                {
                    Status = "error", ExitCode = 1, Cwd = cwd,
                    Stderr = "sql: missing query", DurationMs = sw.ElapsedMilliseconds
                };
            }

            var rows = ExecuteQueryToList(connRef, query, opts);
            return new LlmResult
            {
                Status = "success", ExitCode = 0, Cwd = cwd,
                Stdout = rows,
                StdoutType = "json/rows",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new LlmResult
            {
                Status = "error", ExitCode = 1, Cwd = cwd,
                Stderr = $"sql: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    // ── Internal Dispatch ───────────────────────────────────────────

    private static bool ExecuteInternal(string argsStr, bool forLlm)
    {
        if (string.IsNullOrWhiteSpace(argsStr))
        {
            PrintUsage();
            return true;
        }

        // Check for subcommands first
        var firstWord = argsStr.Split(' ', 2)[0].ToLowerInvariant();
        switch (firstWord)
        {
            case "add": return HandleAdd(argsStr.Length > 3 ? argsStr[3..].TrimStart() : "");
            case "list": return HandleList();
            case "test": return HandleTest(argsStr.Length > 4 ? argsStr[4..].TrimStart() : "");
            case "remove": return HandleRemove(argsStr.Length > 6 ? argsStr[6..].TrimStart() : "");
        }

        // Parse query
        var (opts, remaining) = ParseOptions(argsStr);
        var (connRef, query) = ParseConnectionAndQuery(remaining);

        if (string.IsNullOrEmpty(query))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("sql: missing query. Usage: sql @name \"SELECT ...\"");
            Console.ResetColor();
            return false;
        }

        return ExecuteQuery(connRef, query, opts);
    }

    // ── Query Execution ─────────────────────────────────────────────

    private static bool ExecuteQuery(string connRef, string query, SqlOptions opts)
    {
        var sw = Stopwatch.StartNew();

        using var conn = ResolveConnection(connRef);
        conn.ConnectionTimeout.ToString(); // force init
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        cmd.CommandTimeout = opts.TimeoutSeconds;

        // Non-query detection (INSERT, UPDATE, DELETE, CREATE, DROP, ALTER)
        var trimUpper = query.TrimStart().ToUpperInvariant();
        if (trimUpper.StartsWith("INSERT") || trimUpper.StartsWith("UPDATE") ||
            trimUpper.StartsWith("DELETE") || trimUpper.StartsWith("CREATE") ||
            trimUpper.StartsWith("DROP") || trimUpper.StartsWith("ALTER") ||
            trimUpper.StartsWith("PRAGMA") && !trimUpper.Contains("TABLE_INFO"))
        {
            var affected = cmd.ExecuteNonQuery();
            sw.Stop();

            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"{affected} row(s) affected ({sw.ElapsedMilliseconds}ms)");
            Console.ResetColor();
            return true;
        }

        using var reader = cmd.ExecuteReader();
        sw.Stop();

        switch (opts.OutputFormat)
        {
            case OutputFormat.Json:
                RenderJson(reader, opts);
                break;
            case OutputFormat.Csv:
                RenderCsv(reader, opts);
                break;
            default:
                RenderTable(reader, opts, sw.ElapsedMilliseconds);
                break;
        }

        return true;
    }

    /// <summary>
    /// Execute a query and return all rows as List of Dictionary — used by LLM mode.
    /// </summary>
    private static List<Dictionary<string, object?>> ExecuteQueryToList(
        string connRef, string query, SqlOptions opts)
    {
        using var conn = ResolveConnection(connRef);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        cmd.CommandTimeout = opts.TimeoutSeconds;

        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        var colCount = reader.FieldCount;
        int rowCount = 0;

        while (reader.Read())
        {
            if (opts.RowLimit > 0 && rowCount >= opts.RowLimit)
                break;

            var row = new Dictionary<string, object?>();
            for (int i = 0; i < colCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
            rowCount++;
        }

        return rows;
    }

    // ── Output: Table ───────────────────────────────────────────────

    private static void RenderTable(DbDataReader reader, SqlOptions opts, long elapsedMs)
    {
        var colCount = reader.FieldCount;
        if (colCount == 0) return;

        // Read column names
        var colNames = new string[colCount];
        for (int i = 0; i < colCount; i++)
            colNames[i] = reader.GetName(i);

        // Read all rows into memory (for column width calculation)
        var rows = new List<string[]>();
        int rowCount = 0;
        bool limitHit = false;

        while (reader.Read())
        {
            if (opts.RowLimit > 0 && rowCount >= opts.RowLimit)
            {
                limitHit = true;
                break;
            }

            var row = new string[colCount];
            for (int i = 0; i < colCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? opts.NullDisplay : FormatValue(reader.GetValue(i));
            }
            rows.Add(row);
            rowCount++;
        }

        // Calculate column widths
        var termWidth = Console.IsOutputRedirected ? 200 : Console.WindowWidth;
        var colWidths = new int[colCount];
        for (int i = 0; i < colCount; i++)
            colWidths[i] = colNames[i].Length;
        foreach (var row in rows)
        {
            for (int i = 0; i < colCount; i++)
                colWidths[i] = Math.Max(colWidths[i], row[i].Length);
        }

        // Cap total width to terminal — truncate widest columns if needed
        var gap = 2; // spaces between columns
        var totalWidth = colWidths.Sum() + gap * (colCount - 1);
        if (totalWidth > termWidth && colCount > 1)
        {
            var maxColWidth = (termWidth - gap * (colCount - 1)) / colCount;
            maxColWidth = Math.Max(maxColWidth, 10);
            for (int i = 0; i < colCount; i++)
                colWidths[i] = Math.Min(colWidths[i], maxColWidth);
        }

        bool useColor = !Console.IsOutputRedirected;

        // Header
        if (useColor) Console.ForegroundColor = Theme.Current.TableHeader;
        for (int i = 0; i < colCount; i++)
        {
            if (i > 0) Console.Write(new string(' ', gap));
            Console.Write(Pad(colNames[i], colWidths[i]));
        }
        Console.WriteLine();

        // Separator
        if (useColor) Console.ForegroundColor = Theme.Current.Separator;
        for (int i = 0; i < colCount; i++)
        {
            if (i > 0) Console.Write(new string(' ', gap));
            Console.Write(new string('─', colWidths[i]));
        }
        Console.WriteLine();

        // Rows
        if (useColor) Console.ResetColor();
        foreach (var row in rows)
        {
            for (int i = 0; i < colCount; i++)
            {
                if (i > 0) Console.Write(new string(' ', gap));
                var val = row[i];
                if (useColor && val == opts.NullDisplay)
                    Console.ForegroundColor = Theme.Current.Muted;
                Console.Write(Pad(val, colWidths[i]));
                if (useColor && val == opts.NullDisplay)
                    Console.ResetColor();
            }
            Console.WriteLine();
        }

        // Footer
        if (useColor) Console.ForegroundColor = Theme.Current.Muted;
        var limitNote = limitHit ? $" (limit {opts.RowLimit})" : "";
        Console.WriteLine($"\n{rows.Count} row(s){limitNote} ({elapsedMs}ms)");
        if (useColor) Console.ResetColor();
    }

    private static string Pad(string text, int width)
    {
        if (text.Length > width)
            return text[..(width - 1)] + "…";
        return text.PadRight(width);
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss"),
            byte[] bytes => $"<{bytes.Length} bytes>",
            float f => f.ToString("G"),
            double d => d.ToString("G"),
            decimal dec => dec.ToString("G"),
            _ => value.ToString() ?? ""
        };
    }

    // ── Output: JSON ────────────────────────────────────────────────

    private static void RenderJson(DbDataReader reader, SqlOptions opts)
    {
        var colCount = reader.FieldCount;
        var rows = new List<Dictionary<string, object?>>();
        int rowCount = 0;

        while (reader.Read())
        {
            if (opts.RowLimit > 0 && rowCount >= opts.RowLimit)
                break;

            var row = new Dictionary<string, object?>();
            for (int i = 0; i < colCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = reader.IsDBNull(i) ? null : NormalizeJsonValue(reader.GetValue(i));
            }
            rows.Add(row);
            rowCount++;
        }

        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        Console.WriteLine(json);
    }

    private static object? NormalizeJsonValue(object value)
    {
        return value switch
        {
            DateTime dt => dt.ToString("o"),
            DateTimeOffset dto => dto.ToString("o"),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => value
        };
    }

    // ── Output: CSV ─────────────────────────────────────────────────

    private static void RenderCsv(DbDataReader reader, SqlOptions opts)
    {
        var colCount = reader.FieldCount;

        // Header
        var header = new string[colCount];
        for (int i = 0; i < colCount; i++)
            header[i] = CsvEscape(reader.GetName(i));
        Console.WriteLine(string.Join(",", header));

        // Rows
        int rowCount = 0;
        while (reader.Read())
        {
            if (opts.RowLimit > 0 && rowCount >= opts.RowLimit)
                break;

            var values = new string[colCount];
            for (int i = 0; i < colCount; i++)
                values[i] = reader.IsDBNull(i) ? "" : CsvEscape(FormatValue(reader.GetValue(i)));
            Console.WriteLine(string.Join(",", values));
            rowCount++;
        }
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    // ── Connection Resolution ───────────────────────────────────────

    private static DbConnection ResolveConnection(string connRef)
    {
        // Named connection: @name
        if (connRef.StartsWith('@'))
        {
            var config = SqlConnectionConfig.Load();
            var entry = config.GetConnection(connRef);
            if (entry == null)
                throw new ArgumentException(
                    $"unknown connection '{connRef}'. Run 'sql list' to see connections.");
            return SqlDriverManager.CreateConnection(entry);
        }

        // Inline URI: scheme://...
        if (connRef.Contains("://"))
            return SqlDriverManager.CreateConnectionFromUri(connRef);

        throw new ArgumentException(
            $"invalid connection reference '{connRef}'. Use @name or scheme://...");
    }

    // ── Argument Parsing ────────────────────────────────────────────

    private record SqlOptions
    {
        public OutputFormat OutputFormat { get; set; } = OutputFormat.Table;
        public int RowLimit { get; set; } = 1000;
        public int TimeoutSeconds { get; set; } = 30;
        public string NullDisplay { get; set; } = "NULL";
    }

    private enum OutputFormat { Table, Json, Csv }

    private static (SqlOptions opts, string remaining) ParseOptions(string argsStr)
    {
        var config = SqlConnectionConfig.Load();
        var opts = new SqlOptions
        {
            RowLimit = config.Defaults.RowLimit,
            TimeoutSeconds = config.Defaults.TimeoutSeconds,
            NullDisplay = config.Defaults.NullDisplay
        };

        var remaining = new StringBuilder();
        var parts = TokenizeArgs(argsStr);
        int i = 0;

        while (i < parts.Count)
        {
            var part = parts[i];
            switch (part.ToLowerInvariant())
            {
                case "--json":
                    opts.OutputFormat = OutputFormat.Json;
                    break;
                case "--csv":
                    opts.OutputFormat = OutputFormat.Csv;
                    break;
                case "--limit":
                    if (i + 1 < parts.Count && int.TryParse(parts[i + 1], out var limit))
                    {
                        opts.RowLimit = limit;
                        i++;
                    }
                    break;
                case "--no-limit":
                    opts.RowLimit = 0;
                    break;
                case "--timeout":
                    if (i + 1 < parts.Count && int.TryParse(parts[i + 1], out var timeout))
                    {
                        opts.TimeoutSeconds = timeout;
                        i++;
                    }
                    break;
                default:
                    if (remaining.Length > 0) remaining.Append(' ');
                    remaining.Append(part);
                    break;
            }
            i++;
        }

        return (opts, remaining.ToString());
    }

    /// <summary>
    /// Parse connection reference and query from the remaining args.
    /// The connection ref is the first whitespace-delimited token.
    /// The query is everything after it, with outer quotes stripped but
    /// internal content preserved exactly (including internal quotes).
    /// </summary>
    private static (string connRef, string query) ParseConnectionAndQuery(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input))
            return ("", "");

        // First token (connection ref) — never quoted
        var spaceIdx = input.IndexOf(' ');
        if (spaceIdx < 0)
            return (input, ""); // just a connection ref, no query

        var connRef = input[..spaceIdx];
        var rest = input[(spaceIdx + 1)..].Trim();

        // Strip one layer of outer quotes from the query, preserving internals
        if (rest.Length >= 2 &&
            ((rest[0] == '"' && rest[^1] == '"') ||
             (rest[0] == '\'' && rest[^1] == '\'')))
        {
            rest = rest[1..^1];
        }

        return (connRef, rest);
    }

    /// <summary>
    /// Simple tokenizer that respects quoted strings.
    /// "hello world" → single token. Strips outer quotes.
    /// </summary>
    private static List<string> TokenizeArgs(string input)
    {
        var tokens = new List<string>();
        int i = 0;

        while (i < input.Length)
        {
            // Skip whitespace
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i >= input.Length) break;

            if (input[i] == '"' || input[i] == '\'')
            {
                // Quoted string
                var quote = input[i];
                i++;
                var start = i;
                while (i < input.Length && input[i] != quote) i++;
                tokens.Add(input[start..i]);
                if (i < input.Length) i++; // skip closing quote
            }
            else
            {
                // Unquoted token
                var start = i;
                while (i < input.Length && !char.IsWhiteSpace(input[i])) i++;
                tokens.Add(input[start..i]);
            }
        }

        return tokens;
    }

    // ── Subcommands ─────────────────────────────────────────────────

    private static bool HandleAdd(string argsStr)
    {
        var parts = TokenizeArgs(argsStr);
        if (parts.Count < 1 || !parts[0].StartsWith('@'))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("sql add: usage: sql add @name --driver sqlite --path ~/data/db.sqlite");
            Console.ResetColor();
            return false;
        }

        var name = parts[0];
        var entry = new ConnectionEntry();

        for (int i = 1; i < parts.Count; i++)
        {
            var flag = parts[i].ToLowerInvariant();
            var value = i + 1 < parts.Count ? parts[i + 1] : null;

            switch (flag)
            {
                case "--driver":
                    entry.Driver = value ?? "";
                    i++;
                    break;
                case "--path":
                    entry.Path = value;
                    i++;
                    break;
                case "--host":
                    entry.Host = value;
                    i++;
                    break;
                case "--port":
                    if (int.TryParse(value, out var port)) entry.Port = port;
                    i++;
                    break;
                case "--database" or "--db":
                    entry.Database = value;
                    i++;
                    break;
                case "--user":
                    entry.User = value;
                    i++;
                    break;
                case "--dsn":
                    entry.Dsn = value;
                    i++;
                    break;
                case "--connection-string":
                    entry.ConnectionString = value;
                    i++;
                    break;
                case "--password-env":
                    entry.PasswordEnvVar = value;
                    i++;
                    break;
            }
        }

        if (string.IsNullOrEmpty(entry.Driver))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("sql add: --driver is required (sqlite, postgres, odbc)");
            Console.ResetColor();
            return false;
        }

        var config = SqlConnectionConfig.Load();
        config.SetConnection(name, entry);
        config.Save();

        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine($"  connection {name} added (driver: {entry.Driver})");
        Console.ResetColor();
        return true;
    }

    private static bool HandleList()
    {
        var config = SqlConnectionConfig.Load();

        if (config.Connections.Count == 0)
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  no connections configured. Use 'sql add @name --driver ...' to add one.");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = Theme.Current.Banner;
        Console.WriteLine("  Connections:");
        Console.ResetColor();

        foreach (var (name, entry) in config.Connections)
        {
            Console.Write("  ");
            Console.ForegroundColor = Theme.Current.Accent;
            Console.Write($"@{name}");
            Console.ResetColor();
            Console.Write("  ");
            Console.ForegroundColor = Theme.Current.Muted;

            var detail = entry.Driver.ToLowerInvariant() switch
            {
                "sqlite" => entry.Path ?? "in-memory",
                "postgres" or "postgresql" => $"{entry.Host ?? "localhost"}:{(entry.Port > 0 ? entry.Port : 5432)}/{entry.Database ?? "?"}",
                "odbc" => entry.Dsn ?? entry.ConnectionString ?? "?",
                _ => entry.Driver
            };
            Console.WriteLine($"({entry.Driver}) {detail}");
            Console.ResetColor();
        }

        return true;
    }

    private static bool HandleTest(string argsStr)
    {
        var name = argsStr.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("sql test: usage: sql test @name");
            Console.ResetColor();
            return false;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            using var conn = ResolveConnection(name);
            conn.Open();
            sw.Stop();

            Console.ForegroundColor = Theme.Current.PromptSuccess;
            Console.Write("  ✓");
            Console.ResetColor();
            Console.Write($" {name} connected");
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($" ({sw.ElapsedMilliseconds}ms)");
            Console.ResetColor();
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = Theme.Current.PromptFailed;
            Console.Write("  ✗");
            Console.ResetColor();
            Console.Write($" {name}: ");
            Console.ForegroundColor = Theme.Current.Error;
            Console.WriteLine(ex.Message);
            Console.ResetColor();
            return false;
        }
    }

    private static bool HandleRemove(string argsStr)
    {
        var name = argsStr.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("sql remove: usage: sql remove @name");
            Console.ResetColor();
            return false;
        }

        var config = SqlConnectionConfig.Load();
        if (config.RemoveConnection(name))
        {
            config.Save();
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  connection {name} removed");
            Console.ResetColor();
            return true;
        }
        else
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine($"sql remove: unknown connection '{name}'");
            Console.ResetColor();
            return false;
        }
    }

    // ── Help ────────────────────────────────────────────────────────

    private static void PrintUsage()
    {
        Console.ForegroundColor = Theme.Current.Banner;
        Console.WriteLine("  sql — query databases from the shell");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  Query:   sql @name \"SELECT * FROM users\"");
        Console.WriteLine("           sql sqlite:///path/to/db \"SELECT 1\"");
        Console.WriteLine("           sql postgres://user:pass@host/db \"SELECT ...\"");
        Console.WriteLine();
        Console.WriteLine("  Flags:   --json  --csv  --limit N  --no-limit  --timeout N");
        Console.WriteLine();
        Console.WriteLine("  Manage:  sql add @name --driver sqlite --path ~/data.db");
        Console.WriteLine("           sql add @name --driver postgres --host localhost --database mydb --user admin");
        Console.WriteLine("           sql list | test @name | remove @name");
        Console.WriteLine();
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine("  Drivers: sqlite, postgres, odbc");
        Console.WriteLine("  Config:  ~/.config/rush/databases.json");
        Console.ResetColor();
    }
}
