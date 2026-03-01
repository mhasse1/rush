using System.Management.Automation;

namespace Rush;

/// <summary>
/// Renders PSObject collections as clean, formatted output.
/// Knows about common PowerShell types and picks the best display for each.
/// </summary>
public static class OutputRenderer
{
    // Type-specific property sets for clean output
    private static readonly Dictionary<string, string[]> TypeDisplayProperties = new()
    {
        ["System.IO.DirectoryInfo"] = ["Name", "LastWriteTime"],
        ["System.IO.FileInfo"] = ["Name", "Length", "LastWriteTime"],
        ["System.Diagnostics.Process"] = ["Id", "ProcessName", "WorkingSet64", "CPU"],
        ["System.Management.Automation.PathInfo"] = [], // Use ToString()
    };

    public static void Render(IReadOnlyList<PSObject> results)
    {
        if (results.Count == 0) return;

        // Single result or collection of simple values: print simply
        if (results.Count == 1)
        {
            var single = results[0];
            if (IsSimpleValue(single) || IsPathInfo(single))
            {
                Console.WriteLine(single.ToString());
                return;
            }
        }
        else if (results.All(r => IsSimpleValue(r)))
        {
            // Collection of strings/ints/etc — just print each value
            foreach (var r in results)
                Console.WriteLine(r.ToString());
            return;
        }

        // Check if these are file system items — render ls-style
        var baseTypeName = GetBaseTypeName(results[0]);

        if (baseTypeName is "System.IO.DirectoryInfo" or "System.IO.FileInfo"
            || IsMixedFileSystemItems(results))
        {
            RenderFileSystemItems(results);
            return;
        }

        // Check for type-specific display properties
        if (baseTypeName != null && TypeDisplayProperties.TryGetValue(baseTypeName, out var typeProps) && typeProps.Length > 0)
        {
            RenderTable(results, typeProps);
            return;
        }

        // Generic: pick reasonable properties
        var properties = GetDisplayProperties(results[0]);
        if (properties.Length == 0)
        {
            foreach (var result in results)
                Console.WriteLine(result.ToString());
            return;
        }

        RenderTable(results, properties);
    }

    private static void RenderFileSystemItems(IReadOnlyList<PSObject> results)
    {
        // ls-style output: type indicator, permissions-ish, size, date, name
        foreach (var item in results)
        {
            var baseObj = item.BaseObject;
            var name = GetPropStr(item, "Name");
            var lastWrite = GetPropStr(item, "LastWriteTime");
            bool isDir = baseObj is System.IO.DirectoryInfo;

            if (isDir)
            {
                // Shorten the date
                var dateStr = FormatDate(item, "LastWriteTime");

                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write("d ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{"",10}  {dateStr}  ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"{name}/");
                Console.ResetColor();
            }
            else
            {
                var size = FormatSize(item);
                var dateStr = FormatDate(item, "LastWriteTime");

                Console.Write("- ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{size,10}  {dateStr}  ");
                Console.ResetColor();
                Console.WriteLine(name);
            }
        }
    }

    private static void RenderTable(IReadOnlyList<PSObject> results, string[] properties)
    {
        // Calculate column widths
        var widths = new int[properties.Length];
        for (int i = 0; i < properties.Length; i++)
            widths[i] = properties[i].Length;

        var rows = new List<string[]>();
        foreach (var result in results)
        {
            var row = new string[properties.Length];
            for (int i = 0; i < properties.Length; i++)
            {
                var val = GetPropStr(result, properties[i]);
                row[i] = val;
                widths[i] = Math.Max(widths[i], val.Length);
            }
            rows.Add(row);
        }

        // Cap column widths to terminal width
        int termWidth;
        try { termWidth = Console.WindowWidth - 1; }
        catch { termWidth = 119; }

        int totalWidth = widths.Sum() + (properties.Length - 1) * 3;
        if (totalWidth > termWidth)
        {
            var maxColWidth = Math.Max(12, (termWidth - (properties.Length - 1) * 3) / properties.Length);
            for (int i = 0; i < widths.Length; i++)
                widths[i] = Math.Min(widths[i], maxColWidth);
        }

        // Header
        Console.ForegroundColor = ConsoleColor.Cyan;
        for (int i = 0; i < properties.Length; i++)
        {
            if (i > 0) Console.Write("   ");
            Console.Write(PadOrTruncate(properties[i], widths[i]));
        }
        Console.WriteLine();

        // Separator
        Console.ForegroundColor = ConsoleColor.DarkGray;
        for (int i = 0; i < properties.Length; i++)
        {
            if (i > 0) Console.Write("   ");
            Console.Write(new string('─', widths[i]));
        }
        Console.WriteLine();
        Console.ResetColor();

        // Rows
        foreach (var row in rows)
        {
            for (int i = 0; i < properties.Length; i++)
            {
                if (i > 0) Console.Write("   ");
                Console.Write(PadOrTruncate(row[i], widths[i]));
            }
            Console.WriteLine();
        }
    }

    public static void RenderErrors(PSDataStreams streams)
    {
        var prevColor = Console.ForegroundColor;
        foreach (var error in streams.Error)
        {
            Console.ForegroundColor = ConsoleColor.Red;

            // Extract the cleanest error message — skip stack traces and PS noise
            string msg;
            if (error.Exception != null)
            {
                msg = error.Exception.InnerException?.Message ?? error.Exception.Message;
            }
            else
            {
                msg = error.ToString();
            }

            // Strip common PS error prefixes
            if (msg.Contains("CommandNotFoundException"))
            {
                var cmdName = error.TargetObject?.ToString() ?? "unknown";
                Console.Error.WriteLine($"error: command not found: {cmdName}");
            }
            else if (msg.Contains("ItemNotFoundException"))
            {
                Console.Error.WriteLine($"error: no such file or directory: {error.TargetObject}");
            }
            else if (msg.Contains("ParameterBindingException"))
            {
                Console.Error.WriteLine($"error: invalid argument: {msg.Split(':').LastOrDefault()?.Trim() ?? msg}");
            }
            else
            {
                Console.Error.WriteLine($"error: {msg}");
            }
        }
        Console.ForegroundColor = prevColor;
    }

    private static bool IsSimpleValue(PSObject obj)
    {
        var baseObj = obj.BaseObject;
        return baseObj is string or int or long or double or float or bool or decimal
            or DateTime or Guid;
    }

    private static bool IsPathInfo(PSObject obj)
    {
        return obj.BaseObject.GetType().FullName == "System.Management.Automation.PathInfo";
    }

    private static bool IsMixedFileSystemItems(IReadOnlyList<PSObject> results)
    {
        return results.Any(r => r.BaseObject is System.IO.DirectoryInfo or System.IO.FileInfo);
    }

    private static string? GetBaseTypeName(PSObject obj)
    {
        return obj.BaseObject?.GetType().FullName;
    }

    private static string[] GetDisplayProperties(PSObject obj)
    {
        return obj.Properties
            .Where(p => p.MemberType == PSMemberTypes.Property
                     || p.MemberType == PSMemberTypes.NoteProperty)
            .Select(p => p.Name)
            .Where(name => !name.StartsWith("PS"))
            .Take(6)
            .ToArray();
    }

    private static string GetPropStr(PSObject obj, string propertyName)
    {
        try
        {
            var prop = obj.Properties[propertyName];
            if (prop?.Value == null) return "";
            return prop.Value.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string FormatSize(PSObject obj)
    {
        try
        {
            var prop = obj.Properties["Length"];
            if (prop?.Value is long size)
            {
                if (size < 1024) return $"{size} B";
                if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
                if (size < 1024 * 1024 * 1024) return $"{size / (1024.0 * 1024):F1} MB";
                return $"{size / (1024.0 * 1024 * 1024):F1} GB";
            }
            return prop?.Value?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string FormatDate(PSObject obj, string propName)
    {
        try
        {
            var prop = obj.Properties[propName];
            if (prop?.Value is DateTime dt)
            {
                if (dt.Year == DateTime.Now.Year)
                    return dt.ToString("MMM dd HH:mm");
                return dt.ToString("MMM dd  yyyy");
            }
            return prop?.Value?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string PadOrTruncate(string value, int width)
    {
        if (value.Length > width)
            return value[..(width - 1)] + "…";
        return value.PadRight(width);
    }
}
