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

    // File extension color groups
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".sh", ".bat", ".cmd", ".ps1", ".py", ".rb", ".pl" };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".zip", ".tar", ".gz", ".bz2", ".xz", ".7z", ".rar", ".tgz", ".zst" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".ico", ".webp" };
    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".json", ".yaml", ".yml", ".toml", ".xml", ".ini", ".conf", ".cfg", ".env" };
    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".md", ".txt", ".rst", ".doc", ".docx", ".pdf" };

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

        // Process objects — custom rendering with human-readable memory
        if (baseTypeName == "System.Diagnostics.Process")
        {
            RenderProcesses(results);
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

                Console.ForegroundColor = Theme.Current.Directory;
                Console.Write("d ");
                Console.ForegroundColor = Theme.Current.Metadata;
                Console.Write($"{"",10}  {dateStr}  ");
                Console.ForegroundColor = Theme.Current.Directory;
                Console.WriteLine($"{name}/");
                Console.ResetColor();
            }
            else
            {
                var size = FormatSize(item);
                var dateStr = FormatDate(item, "LastWriteTime");

                Console.Write("- ");
                Console.ForegroundColor = Theme.Current.Metadata;
                Console.Write($"{size,10}  {dateStr}  ");
                Console.ForegroundColor = GetFileColor(name);
                Console.WriteLine(name);
                Console.ResetColor();
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
        Console.ForegroundColor = Theme.Current.TableHeader;
        for (int i = 0; i < properties.Length; i++)
        {
            if (i > 0) Console.Write("   ");
            Console.Write(PadOrTruncate(properties[i], widths[i]));
        }
        Console.WriteLine();

        // Separator
        Console.ForegroundColor = Theme.Current.Separator;
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
            Console.ForegroundColor = Theme.Current.Error;

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

            // Classify by exception type for clean messages
            var exTypeName = error.Exception?.GetType().Name ?? "";
            var errorId = error.FullyQualifiedErrorId ?? "";

            if (exTypeName.Contains("CommandNotFoundException") || errorId.Contains("CommandNotFoundException"))
            {
                var cmdName = error.TargetObject?.ToString() ?? "unknown";
                Console.Error.WriteLine($"error: command not found: {cmdName}");
            }
            else if (exTypeName.Contains("ItemNotFoundException") || errorId.Contains("ItemNotFound"))
            {
                Console.Error.WriteLine($"error: no such file or directory: {error.TargetObject}");
            }
            else if (exTypeName.Contains("ParameterBindingException"))
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

    private static void RenderProcesses(IReadOnlyList<PSObject> results)
    {
        // Calculate column widths for process display
        var rows = new List<(string id, string name, string mem, string cpu)>();
        int maxIdW = 3, maxNameW = 4, maxMemW = 6, maxCpuW = 3;

        foreach (var result in results)
        {
            var id = GetPropStr(result, "Id");
            var name = GetPropStr(result, "ProcessName");
            var memStr = FormatMemory(result);
            var cpuStr = FormatCpu(result);

            maxIdW = Math.Max(maxIdW, id.Length);
            maxNameW = Math.Max(maxNameW, name.Length);
            maxMemW = Math.Max(maxMemW, memStr.Length);
            maxCpuW = Math.Max(maxCpuW, cpuStr.Length);

            rows.Add((id, name, memStr, cpuStr));
        }

        // Cap name width
        maxNameW = Math.Min(maxNameW, 30);

        // Header
        Console.ForegroundColor = Theme.Current.TableHeader;
        Console.Write(PadOrTruncate("PID", maxIdW));
        Console.Write("   ");
        Console.Write(PadOrTruncate("Name", maxNameW));
        Console.Write("   ");
        Console.Write(PadOrTruncate("Memory", maxMemW));
        Console.Write("   ");
        Console.Write(PadOrTruncate("CPU", maxCpuW));
        Console.WriteLine();

        // Separator
        Console.ForegroundColor = Theme.Current.Separator;
        Console.Write(new string('─', maxIdW));
        Console.Write("   ");
        Console.Write(new string('─', maxNameW));
        Console.Write("   ");
        Console.Write(new string('─', maxMemW));
        Console.Write("   ");
        Console.Write(new string('─', maxCpuW));
        Console.WriteLine();
        Console.ResetColor();

        // Rows
        foreach (var (id, name, mem, cpu) in rows)
        {
            Console.ForegroundColor = Theme.Current.Metadata;
            Console.Write(PadOrTruncate(id, maxIdW));
            Console.Write("   ");
            Console.ResetColor();
            Console.Write(PadOrTruncate(name, maxNameW));
            Console.Write("   ");
            Console.ForegroundColor = Theme.Current.Memory;
            Console.Write(PadOrTruncate(mem, maxMemW));
            Console.Write("   ");
            Console.ResetColor();
            Console.Write(PadOrTruncate(cpu, maxCpuW));
            Console.WriteLine();
        }
        Console.ResetColor();
    }

    private static string FormatMemory(PSObject obj)
    {
        try
        {
            var prop = obj.Properties["WorkingSet64"];
            if (prop?.Value is long bytes)
            {
                if (bytes < 1024) return $"{bytes} B";
                if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
                if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
                return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            }
            return prop?.Value?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string FormatCpu(PSObject obj)
    {
        try
        {
            var prop = obj.Properties["CPU"];
            if (prop?.Value is double cpu)
            {
                if (cpu < 0.01) return "0.0";
                if (cpu < 100) return $"{cpu:F1}";
                return $"{cpu:F0}";
            }
            return prop?.Value?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static ConsoleColor GetFileColor(string name)
    {
        var ext = System.IO.Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext))
        {
            // Hidden files (dotfiles)
            if (name.StartsWith('.')) return Theme.Current.Muted;
            return Theme.Current.RegularFile;
        }

        if (ExecutableExtensions.Contains(ext)) return Theme.Current.Executable;
        if (ArchiveExtensions.Contains(ext)) return Theme.Current.Archive;
        if (ImageExtensions.Contains(ext)) return Theme.Current.Image;
        if (ConfigExtensions.Contains(ext)) return Theme.Current.Config;
        if (DocExtensions.Contains(ext)) return Theme.Current.Document;

        // Source code files
        if (ext is ".cs" or ".js" or ".ts" or ".go" or ".rs" or ".java" or ".c" or ".cpp" or ".h"
            or ".swift" or ".kt" or ".dart" or ".vue" or ".svelte" or ".jsx" or ".tsx")
            return Theme.Current.SourceCode;

        return Theme.Current.RegularFile;
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
