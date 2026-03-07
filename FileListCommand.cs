using System.Runtime.InteropServices;
using System.Text;

namespace Rush;

/// <summary>
/// Unix-like `ls` builtin command. Bypasses PowerShell pipeline for direct
/// .NET file enumeration, adaptive multi-column output, and full -l format
/// with permissions, owner/group, symlinks.
///
/// When ls is piped (ls | grep foo), it falls through to the existing
/// Get-ChildItem translation path so downstream commands get PSObjects.
/// </summary>
public static class FileListCommand
{
    // ── File Extension Color Groups (shared with OutputRenderer) ─────────

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

    // ── Options ──────────────────────────────────────────────────────────

    private record LsOptions
    {
        public bool Long { get; init; }          // -l
        public bool All { get; init; }           // -a
        public bool Recursive { get; init; }     // -R
        public bool Reverse { get; init; }       // -r
        public bool SortByTime { get; init; }    // -t
        public bool SortBySize { get; init; }    // -S
        public bool OnePerLine { get; init; }    // -1
        public bool DirOnly { get; init; }       // -d
        public bool TypeIndicator { get; init; } // -F
        public bool NoGroup { get; init; }       // -G
        public bool HumanReadable { get; init; } = true; // -h (always on)
        public List<string> Paths { get; init; } = new();
    }

    // ── Entry Point ──────────────────────────────────────────────────────

    /// <summary>
    /// Execute the ls builtin. Returns true on success, false on error.
    /// </summary>
    public static bool Execute(string argsStr)
    {
        var opts = ParseArgs(argsStr);
        var paths = opts.Paths.Count > 0 ? opts.Paths : new List<string> { "." };
        bool multiPath = paths.Count > 1;
        bool anyError = false;

        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];

            // Expand ~ to home directory
            if (path.StartsWith("~/") || path == "~")
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = path == "~" ? home : Path.Combine(home, path[2..]);
            }

            // Resolve relative paths
            path = Path.GetFullPath(path);

            if (multiPath && i > 0) Console.WriteLine();

            if (opts.DirOnly)
            {
                // -d: list the directory entry itself, not contents
                if (Directory.Exists(path) || File.Exists(path))
                {
                    var info = Directory.Exists(path)
                        ? (FileSystemInfo)new DirectoryInfo(path)
                        : new FileInfo(path);
                    var entries = new List<FileSystemInfo> { info };
                    if (opts.Long)
                        RenderLong(entries, opts);
                    else
                        RenderColumns(entries, opts);
                }
                else
                {
                    PrintError($"ls: cannot access '{paths[i]}': No such file or directory");
                    anyError = true;
                }
                continue;
            }

            if (File.Exists(path))
            {
                // Single file — just list it
                var info = new FileInfo(path);
                var entries = new List<FileSystemInfo> { info };
                if (opts.Long)
                    RenderLong(entries, opts);
                else
                    RenderColumns(entries, opts);
                continue;
            }

            if (!Directory.Exists(path))
            {
                PrintError($"ls: cannot access '{paths[i]}': No such file or directory");
                anyError = true;
                continue;
            }

            if (multiPath)
            {
                Console.ForegroundColor = Theme.Current.Accent;
                Console.WriteLine($"{paths[i]}:");
                Console.ResetColor();
            }

            if (opts.Recursive)
                ListRecursive(path, opts, isRoot: true);
            else
                ListDirectory(path, opts);
        }

        return !anyError;
    }

    // ── Directory Enumeration ────────────────────────────────────────────

    private static void ListDirectory(string path, LsOptions opts)
    {
        var entries = EnumerateEntries(path, opts);
        entries = SortEntries(entries, opts);

        if (opts.Long)
            RenderLong(entries, opts);
        else
            RenderColumns(entries, opts);
    }

    private static void ListRecursive(string path, LsOptions opts, bool isRoot)
    {
        if (!isRoot)
        {
            Console.WriteLine();
            Console.ForegroundColor = Theme.Current.Accent;
            Console.WriteLine($"{path}:");
            Console.ResetColor();
        }

        ListDirectory(path, opts);

        try
        {
            foreach (var subDir in Directory.GetDirectories(path).OrderBy(d => d))
            {
                var dirName = Path.GetFileName(subDir);
                if (!opts.All && dirName.StartsWith('.')) continue;
                ListRecursive(subDir, opts, isRoot: false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            PrintError($"ls: cannot open directory '{path}': Permission denied");
        }
    }

    private static List<FileSystemInfo> EnumerateEntries(string path, LsOptions opts)
    {
        var entries = new List<FileSystemInfo>();

        try
        {
            var di = new DirectoryInfo(path);

            if (opts.All)
            {
                // Add . and .. pseudo-entries
                entries.Add(di);
                if (di.Parent != null)
                    entries.Add(di.Parent);
                else
                    entries.Add(di); // root has .. pointing to itself
            }

            foreach (var entry in di.EnumerateFileSystemInfos())
            {
                if (!opts.All && entry.Name.StartsWith('.'))
                    continue;
                entries.Add(entry);
            }
        }
        catch (UnauthorizedAccessException)
        {
            PrintError($"ls: cannot open directory '{path}': Permission denied");
        }
        catch (Exception ex)
        {
            PrintError($"ls: {ex.Message}");
        }

        return entries;
    }

    private static List<FileSystemInfo> SortEntries(List<FileSystemInfo> entries, LsOptions opts)
    {
        // Preserve . and .. at the top when -a is used
        List<FileSystemInfo>? dotEntries = null;
        if (opts.All && entries.Count >= 2)
        {
            dotEntries = entries.Take(2).ToList();
            entries = entries.Skip(2).ToList();
        }

        IEnumerable<FileSystemInfo> sorted;
        if (opts.SortByTime)
        {
            sorted = entries.OrderByDescending(e => SafeLastWrite(e));
        }
        else if (opts.SortBySize)
        {
            sorted = entries.OrderByDescending(e => e is FileInfo fi ? fi.Length : 0);
        }
        else
        {
            sorted = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        }

        if (opts.Reverse)
            sorted = sorted.Reverse();

        var result = sorted.ToList();
        if (dotEntries != null)
            result.InsertRange(0, dotEntries);

        return result;
    }

    // ── Multi-Column Rendering (bare ls) ─────────────────────────────────

    private static void RenderColumns(List<FileSystemInfo> entries, LsOptions opts)
    {
        if (entries.Count == 0) return;

        // Build display names
        var names = new List<(string display, FileSystemInfo entry, bool isDotPseudo)>();
        bool hasDotEntries = opts.All && entries.Count >= 2;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool isDotPseudo = hasDotEntries && i < 2;
            string display;

            if (isDotPseudo)
            {
                display = i == 0 ? "." : "..";
            }
            else
            {
                display = entry.Name;
            }

            // Add type indicator suffix
            if (entry is DirectoryInfo)
                display += "/";
            else if (opts.TypeIndicator)
            {
                if (IsExecutable(entry))
                    display += "*";
                else if (entry.LinkTarget != null)
                    display += "@";
            }
            else if (entry.LinkTarget != null && !isDotPseudo)
            {
                display += "@";
            }

            names.Add((display, entry, isDotPseudo));
        }

        // Force one-per-line if -1 flag or output is redirected
        if (opts.OnePerLine || Console.IsOutputRedirected)
        {
            foreach (var (display, entry, isDot) in names)
            {
                WriteColoredName(display, entry, isDot);
                Console.WriteLine();
            }
            Console.ResetColor();
            return;
        }

        // Adaptive multi-column layout
        int termWidth;
        try { termWidth = Console.WindowWidth; }
        catch { termWidth = 80; }

        // Find maximum name length
        int maxLen = names.Max(n => n.display.Length);
        int colWidth = maxLen + 2; // 2-space gap between columns
        int numCols = Math.Max(1, termWidth / colWidth);
        int numRows = (names.Count + numCols - 1) / numCols;

        // Column-major ordering (fills down columns, like real ls)
        for (int row = 0; row < numRows; row++)
        {
            for (int col = 0; col < numCols; col++)
            {
                int idx = col * numRows + row;
                if (idx >= names.Count) break;

                var (display, entry, isDot) = names[idx];
                WriteColoredName(display, entry, isDot);

                // Pad to column width (except last column)
                if (col < numCols - 1 && idx + numRows < names.Count)
                {
                    int padding = colWidth - display.Length;
                    if (padding > 0) Console.Write(new string(' ', padding));
                }
            }
            Console.WriteLine();
        }
        Console.ResetColor();
    }

    // ── Long Format Rendering (-l) ───────────────────────────────────────

    private static void RenderLong(List<FileSystemInfo> entries, LsOptions opts)
    {
        if (entries.Count == 0) return;

        bool hasDotEntries = opts.All && entries.Count >= 2;

        // Pre-compute all columns for alignment
        var rows = new List<LongRow>();
        foreach (var entry in entries)
        {
            rows.Add(BuildLongRow(entry, opts));
        }

        // Calculate column widths
        int linkW = rows.Max(r => r.Links.Length);
        int ownerW = rows.Max(r => r.Owner.Length);
        int groupW = opts.NoGroup ? 0 : rows.Max(r => r.Group.Length);
        int sizeW = rows.Max(r => r.Size.Length);

        // Print total line (sum of 512-byte blocks)
        if (!hasDotEntries || entries.Count > 2)
        {
            long totalBlocks = 0;
            int startIdx = hasDotEntries ? 2 : 0;
            for (int i = startIdx; i < entries.Count; i++)
            {
                if (entries[i] is FileInfo fi)
                    totalBlocks += (fi.Length + 511) / 512;
            }
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"total {totalBlocks}");
            Console.ResetColor();
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            bool isDotPseudo = hasDotEntries && i < 2;

            // Permission string with colors
            WritePermissions(row.Permissions);
            Console.Write(" ");

            // Link count
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(row.Links.PadLeft(linkW));
            Console.Write("  ");

            // Owner
            Console.ForegroundColor = Theme.Current.Accent;
            Console.Write(row.Owner.PadRight(ownerW));
            Console.Write("  ");

            // Group
            if (!opts.NoGroup)
            {
                Console.ForegroundColor = Theme.Current.Accent;
                Console.Write(row.Group.PadRight(groupW));
                Console.Write("  ");
            }

            // Size (right-aligned)
            Console.ForegroundColor = Theme.Current.Metadata;
            Console.Write(row.Size.PadLeft(sizeW));
            Console.Write("  ");

            // Date
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(row.Date);
            Console.Write("  ");

            // Name with color
            string displayName;
            if (isDotPseudo)
                displayName = i == 0 ? "." : "..";
            else
                displayName = row.Name;

            WriteColoredName(displayName, entries[i], isDotPseudo);

            // Symlink target
            if (!string.IsNullOrEmpty(row.LinkTarget))
            {
                Console.ForegroundColor = Theme.Current.Muted;
                Console.Write(" -> ");
                Console.Write(row.LinkTarget);
            }

            Console.WriteLine();
        }
        Console.ResetColor();
    }

    private record LongRow
    {
        public string Permissions { get; init; } = "";
        public string Links { get; init; } = "1";
        public string Owner { get; init; } = "";
        public string Group { get; init; } = "";
        public string Size { get; init; } = "";
        public string Date { get; init; } = "";
        public string Name { get; init; } = "";
        public string? LinkTarget { get; init; }
    }

    private static LongRow BuildLongRow(FileSystemInfo entry, LsOptions opts)
    {
        bool isDir = entry is DirectoryInfo;
        bool isLink = entry.LinkTarget != null;
        string typeChar = isLink ? "l" : isDir ? "d" : "-";

        // Permissions
        string perms = GetPermissionString(entry);

        // Owner and Group
        var (owner, group) = GetOwnerGroup(entry);

        // Size
        string size;
        if (isDir)
        {
            size = GetDirectorySizeDisplay(entry);
        }
        else if (entry is FileInfo fi)
        {
            size = FormatSize(fi.Length);
        }
        else
        {
            size = "0";
        }

        // Date
        string date = FormatLsDate(SafeLastWrite(entry));

        // Name
        string name = entry.Name;
        if (isDir)
            name += "/";

        // Link target
        string? linkTarget = entry.LinkTarget;

        return new LongRow
        {
            Permissions = typeChar + perms,
            Links = isDir ? GetHardLinkCount(entry).ToString() : "1",
            Owner = owner,
            Group = group,
            Size = size,
            Date = date,
            Name = name,
            LinkTarget = linkTarget
        };
    }

    // ── Permission String ────────────────────────────────────────────────

    private static string GetPermissionString(FileSystemInfo entry)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: simplified based on attributes
            bool isReadOnly = entry.Attributes.HasFlag(FileAttributes.ReadOnly);
            if (entry is DirectoryInfo)
                return isReadOnly ? "r-xr-xr-x" : "rwxr-xr-x";
            return isReadOnly ? "r--r--r--" : "rw-r--r--";
        }

        // Unix: use .NET 8's UnixFileMode
        try
        {
            var mode = entry.UnixFileMode;
            var sb = new StringBuilder(9);

            sb.Append((mode & UnixFileMode.UserRead) != 0 ? 'r' : '-');
            sb.Append((mode & UnixFileMode.UserWrite) != 0 ? 'w' : '-');
            if ((mode & UnixFileMode.SetUser) != 0)
                sb.Append((mode & UnixFileMode.UserExecute) != 0 ? 's' : 'S');
            else
                sb.Append((mode & UnixFileMode.UserExecute) != 0 ? 'x' : '-');

            sb.Append((mode & UnixFileMode.GroupRead) != 0 ? 'r' : '-');
            sb.Append((mode & UnixFileMode.GroupWrite) != 0 ? 'w' : '-');
            if ((mode & UnixFileMode.SetGroup) != 0)
                sb.Append((mode & UnixFileMode.GroupExecute) != 0 ? 's' : 'S');
            else
                sb.Append((mode & UnixFileMode.GroupExecute) != 0 ? 'x' : '-');

            sb.Append((mode & UnixFileMode.OtherRead) != 0 ? 'r' : '-');
            sb.Append((mode & UnixFileMode.OtherWrite) != 0 ? 'w' : '-');
            if ((mode & UnixFileMode.StickyBit) != 0)
                sb.Append((mode & UnixFileMode.OtherExecute) != 0 ? 't' : 'T');
            else
                sb.Append((mode & UnixFileMode.OtherExecute) != 0 ? 'x' : '-');

            return sb.ToString();
        }
        catch
        {
            return entry is DirectoryInfo ? "rwxr-xr-x" : "rw-r--r--";
        }
    }

    // ── Owner / Group (Unix P/Invoke) ────────────────────────────────────

    // Cache uid→name and gid→name lookups
    private static readonly Dictionary<uint, string> UidNameCache = new();
    private static readonly Dictionary<uint, string> GidNameCache = new();

    // Linux x86_64 stat struct: 144 bytes total
    // dev(8) ino(8) nlink(8) mode(4) uid(4) gid(4) pad(4) rdev(8) size(8) ...
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct LinuxX64StatBuf
    {
        [FieldOffset(28)] public uint st_uid;
        [FieldOffset(32)] public uint st_gid;
    }

    // Linux arm64 stat struct: 128 bytes total
    // dev(8) ino(8) mode(4) nlink(4) uid(4) gid(4) ...
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct LinuxArm64StatBuf
    {
        [FieldOffset(24)] public uint st_uid;
        [FieldOffset(28)] public uint st_gid;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int lstat_linux_x64(string path, out LinuxX64StatBuf buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int lstat_linux_arm64(string path, out LinuxArm64StatBuf buf);

    // macOS arm64 stat: dev(4) mode(2) nlink(2) ino(8) uid(4) gid(4) ...
    [DllImport("libc", SetLastError = true, EntryPoint = "lstat$INODE64")]
    private static extern int lstat_macos(string path, out MacStatBuf buf);

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStatBuf
    {
        [FieldOffset(12)] public uint st_uid;
        [FieldOffset(16)] public uint st_gid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public IntPtr pw_name;
        public IntPtr pw_passwd;
        public uint pw_uid;
        public uint pw_gid;
        // Platform-specific fields follow but we only need pw_name
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Group
    {
        public IntPtr gr_name;
        public IntPtr gr_passwd;
        public uint gr_gid;
        // ... rest not needed
    }

    [DllImport("libc")]
    private static extern IntPtr getpwuid(uint uid);

    [DllImport("libc")]
    private static extern IntPtr getgrgid(uint gid);

    private static (string owner, string group) GetOwnerGroup(FileSystemInfo entry)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (Environment.UserName, "");

        try
        {
            uint uid, gid;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (lstat_macos(entry.FullName, out var macBuf) != 0)
                    return (Environment.UserName, "staff");
                uid = macBuf.st_uid;
                gid = macBuf.st_gid;
            }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                if (lstat_linux_arm64(entry.FullName, out var armBuf) != 0)
                    return (Environment.UserName, Environment.UserName);
                uid = armBuf.st_uid;
                gid = armBuf.st_gid;
            }
            else
            {
                if (lstat_linux_x64(entry.FullName, out var x64Buf) != 0)
                    return (Environment.UserName, Environment.UserName);
                uid = x64Buf.st_uid;
                gid = x64Buf.st_gid;
            }

            // Resolve uid → username
            string owner;
            if (!UidNameCache.TryGetValue(uid, out owner!))
            {
                var pwPtr = getpwuid(uid);
                if (pwPtr != IntPtr.Zero)
                {
                    var pw = Marshal.PtrToStructure<Passwd>(pwPtr);
                    owner = Marshal.PtrToStringAnsi(pw.pw_name) ?? uid.ToString();
                }
                else
                {
                    owner = uid.ToString();
                }
                UidNameCache[uid] = owner;
            }

            // Resolve gid → group name
            string group;
            if (!GidNameCache.TryGetValue(gid, out group!))
            {
                var grPtr = getgrgid(gid);
                if (grPtr != IntPtr.Zero)
                {
                    var gr = Marshal.PtrToStructure<Group>(grPtr);
                    group = Marshal.PtrToStringAnsi(gr.gr_name) ?? gid.ToString();
                }
                else
                {
                    group = gid.ToString();
                }
                GidNameCache[gid] = group;
            }

            return (owner, group);
        }
        catch
        {
            return (Environment.UserName, "staff");
        }
    }

    // ── Formatting Helpers ───────────────────────────────────────────────

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString();
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0}K";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}M";
        return $"{bytes / (1024.0 * 1024 * 1024):F1}G";
    }

    private static string FormatLsDate(DateTime dt)
    {
        // ls format: "Mar  1 14:34" for recent, "Mar  1  2024" for old
        var sixMonthsAgo = DateTime.Now.AddMonths(-6);
        if (dt > sixMonthsAgo && dt <= DateTime.Now)
            return dt.ToString("MMM dd HH:mm");
        return dt.ToString("MMM dd  yyyy");
    }

    private static DateTime SafeLastWrite(FileSystemInfo entry)
    {
        try { return entry.LastWriteTime; }
        catch { return DateTime.MinValue; }
    }

    private static string GetDirectorySizeDisplay(FileSystemInfo entry)
    {
        // Real ls shows the directory's inode allocation size (not contents total)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "-";
        // macOS APFS: typically 64-160 bytes; Linux ext4: typically 4096
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "160" : "4096";
    }

    private static int GetHardLinkCount(FileSystemInfo entry)
    {
        // Approximate: directories get 2 + number of subdirectories
        if (entry is DirectoryInfo di)
        {
            try
            {
                return 2 + di.GetDirectories().Length;
            }
            catch
            {
                return 2;
            }
        }
        return 1;
    }

    private static bool IsExecutable(FileSystemInfo entry)
    {
        if (entry is not FileInfo fi) return false;
        var ext = fi.Extension;
        if (ExecutableExtensions.Contains(ext)) return true;

        // On Unix, check execute permission
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return (entry.UnixFileMode & UnixFileMode.UserExecute) != 0;
            }
            catch { }
        }
        return false;
    }

    // ── Colored Output ───────────────────────────────────────────────────

    private static void WriteColoredName(string display, FileSystemInfo entry, bool isDotPseudo)
    {
        if (isDotPseudo)
        {
            Console.ForegroundColor = Theme.Current.Directory;
            Console.Write(display);
            return;
        }

        if (entry is DirectoryInfo)
        {
            Console.ForegroundColor = Theme.Current.Directory;
            Console.Write(display);
        }
        else if (entry.LinkTarget != null)
        {
            Console.ForegroundColor = Theme.Current.Accent;
            Console.Write(display);
        }
        else
        {
            Console.ForegroundColor = GetFileColor(entry.Name);
            Console.Write(display);
        }
    }

    private static void WritePermissions(string perms)
    {
        // Type character (d, l, -)
        char typeChar = perms[0];
        if (typeChar == 'd')
            Console.ForegroundColor = Theme.Current.Directory;
        else if (typeChar == 'l')
            Console.ForegroundColor = Theme.Current.Accent;
        else
            Console.ForegroundColor = Theme.Current.Muted;
        Console.Write(typeChar);

        // Permission characters with semantic coloring
        for (int i = 1; i < perms.Length; i++)
        {
            char c = perms[i];
            Console.ForegroundColor = c switch
            {
                'r' => Theme.Current.PermRead,
                'w' => Theme.Current.PermWrite,
                'x' or 's' or 't' => Theme.Current.PermExec,
                _ => Theme.Current.Muted // '-', 'S', 'T'
            };
            Console.Write(c);
        }
    }

    private static ConsoleColor GetFileColor(string name)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext))
        {
            if (name.StartsWith('.')) return Theme.Current.Muted;
            return Theme.Current.RegularFile;
        }

        if (ExecutableExtensions.Contains(ext)) return Theme.Current.Executable;
        if (ArchiveExtensions.Contains(ext)) return Theme.Current.Archive;
        if (ImageExtensions.Contains(ext)) return Theme.Current.Image;
        if (ConfigExtensions.Contains(ext)) return Theme.Current.Config;
        if (DocExtensions.Contains(ext)) return Theme.Current.Document;

        if (ext is ".cs" or ".js" or ".ts" or ".go" or ".rs" or ".java" or ".c" or ".cpp" or ".h"
            or ".swift" or ".kt" or ".dart" or ".vue" or ".svelte" or ".jsx" or ".tsx")
            return Theme.Current.SourceCode;

        return Theme.Current.RegularFile;
    }

    // ── Argument Parsing ─────────────────────────────────────────────────

    private static LsOptions ParseArgs(string argsStr)
    {
        bool l = false, a = false, R = false, r = false, t = false;
        bool S = false, one = false, d = false, F = false, G = false;
        var paths = new List<string>();

        var parts = CommandTranslator.SplitCommandLine(argsStr);

        foreach (var part in parts)
        {
            if (part.StartsWith('-') && part.Length > 1 && part[1] != '-')
            {
                // Decompose combined flags: -laSt → l, a, S, t
                foreach (var ch in part[1..])
                {
                    switch (ch)
                    {
                        case 'l': l = true; break;
                        case 'a': a = true; break;
                        case 'A': a = true; break; // -A ≈ -a (close enough)
                        case 'R': R = true; break;
                        case 'r': r = true; break;
                        case 't': t = true; break;
                        case 'S': S = true; break;
                        case '1': one = true; break;
                        case 'd': d = true; break;
                        case 'F': F = true; break;
                        case 'G': G = true; break;
                        case 'h': break; // human-readable: always on
                    }
                }
            }
            else if (part.StartsWith("--"))
            {
                // Long flags (limited support)
                switch (part)
                {
                    case "--all": a = true; break;
                    case "--recursive": R = true; break;
                    case "--reverse": r = true; break;
                    case "--directory": d = true; break;
                    case "--classify": F = true; break;
                    case "--no-group": G = true; break;
                }
            }
            else
            {
                paths.Add(part);
            }
        }

        return new LsOptions
        {
            Long = l,
            All = a,
            Recursive = R,
            Reverse = r,
            SortByTime = t,
            SortBySize = S,
            OnePerLine = one,
            DirOnly = d,
            TypeIndicator = F,
            NoGroup = G,
            Paths = paths
        };
    }

    // ── Error Output ─────────────────────────────────────────────────────

    private static void PrintError(string msg)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  {msg}");
        Console.ResetColor();
    }
}
