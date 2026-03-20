namespace Rush;

/// <summary>
/// In-terminal color selector for picking background colors.
/// Renders a navigable grid of curated colors + dynamic "similar to current" palette.
/// </summary>
public static class ColorPicker
{
    // ── Curated palette data ────────────────────────────────────────

    internal record PaletteEntry(string Hex, string Name);

    private static readonly PaletteEntry[] DarkPalette =
    {
        // Row 1: distinct hues at ~15% lightness
        new("#1A1A2E", "Midnight Blue"),
        new("#16213E", "Navy"),
        new("#0F3460", "Deep Blue"),
        new("#002B36", "Solarized Dark"),
        new("#1B4332", "Forest Green"),
        new("#2D6A4F", "Emerald"),
        new("#3A2E1F", "Espresso"),
        new("#4A2020", "Dark Maroon"),
        new("#6B2020", "Crimson"),
        new("#2E1A47", "Deep Purple"),
        new("#4A1942", "Plum"),
        new("#3D1F4E", "Grape"),
        // Row 2: popular themes + mid-dark hues
        new("#2E3440", "Nord"),
        new("#282A36", "Dracula"),
        new("#282828", "Gruvbox Dark"),
        new("#1E1E2E", "Catppuccin"),
        new("#282C34", "One Dark"),
        new("#1A1B26", "Tokyo Night"),
        new("#263238", "Material Ocean"),
        new("#2D353B", "Everforest"),
        new("#1F3044", "Steel Blue"),
        new("#2A3F54", "Petrol"),
        new("#3B4F2A", "Olive"),
        new("#4E3B31", "Chocolate"),
    };

    private static readonly PaletteEntry[] LightPalette =
    {
        // Distinct tinted lights — each visually different
        new("#FDF6E3", "Solarized Light"),
        new("#FBF1C7", "Gruvbox Cream"),
        new("#FFE8E8", "Rose Blush"),
        new("#E8F0FF", "Ice Blue"),
        new("#E8FFE8", "Mint"),
        new("#FFF0E0", "Peach"),
        new("#F0E0FF", "Lavender"),
        new("#E0F0F0", "Seafoam"),
        new("#FFF8E0", "Butter"),
        new("#FFE0F0", "Pink"),
        new("#E0FFE8", "Spring"),
        new("#F5F5F5", "Paper"),
    };

    private static readonly PaletteEntry[] GrayscalePalette =
    {
        new("#0A0A0A", "Near Black"),
        new("#1A1A1A", "Charcoal"),
        new("#2A2A2A", "Dark Gray"),
        new("#3A3A3A", "Gray 23%"),
        new("#4A4A4A", "Gray 29%"),
        new("#5A5A5A", "Gray 35%"),
        new("#808080", "Mid Gray"),
        new("#A0A0A0", "Gray 63%"),
        new("#B8B8B8", "Gray 72%"),
        new("#D0D0D0", "Light Gray"),
        new("#E0E0E0", "Pale Gray"),
        new("#F0F0F0", "Near White"),
    };

    private const int Columns = 12;

    /// <summary>
    /// Run the interactive color selector. Returns the selected hex color or null if cancelled.
    /// </summary>
    public static string? Run(string? initialColor = null)
    {
        // Build the sections
        var sections = new List<(string Label, PaletteEntry[] Colors)>();
        sections.Add(("Popular Dark", DarkPalette));
        sections.Add(("Popular Light", LightPalette));

        PaletteEntry[]? similarColors = null;
        if (!string.IsNullOrEmpty(initialColor) && Theme.TryParseHexColor(initialColor, out _, out _, out _))
        {
            similarColors = GenerateSimilarColors(initialColor, 12);
            if (similarColors.Length > 0)
                sections.Add(($"Similar to Current ({initialColor})", similarColors));
        }

        sections.Add(("Grayscale", GrayscalePalette));

        // Build flat grid with section tracking
        var cells = new List<PaletteEntry>();
        var sectionStarts = new List<(int CellIndex, string Label)>();
        foreach (var (label, colors) in sections)
        {
            sectionStarts.Add((cells.Count, label));
            cells.AddRange(colors);
        }

        if (cells.Count == 0) return null;

        // Find initial cursor position (closest to current color)
        int cursor = 0;
        if (!string.IsNullOrEmpty(initialColor))
            cursor = FindClosestEntry(cells, initialColor);

        // Save original bg for cancel/restore
        var originalBg = Environment.GetEnvironmentVariable("RUSH_BG");
        var originalCursorVisible = true;
        try { originalCursorVisible = Console.CursorVisible; } catch { }

        // Enter raw mode
        try { Console.CursorVisible = false; } catch { }

        // Hex input mode state
        bool hexInputMode = false;
        string hexBuffer = "#";

        try
        {
            // Initial render + live preview
            Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
            ApplyPreview(cells[cursor].Hex);

            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (hexInputMode)
                {
                    if (key.Key == ConsoleKey.Escape)
                    {
                        hexInputMode = false;
                        hexBuffer = "#";
                        Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                        ApplyPreview(cells[cursor].Hex);
                        continue;
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (hexBuffer.Length > 1)
                            hexBuffer = hexBuffer[..^1];
                        Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                        // Live preview typed hex if valid
                        if ((hexBuffer.Length == 4 || hexBuffer.Length == 7) &&
                            Theme.TryParseHexColor(hexBuffer, out _, out _, out _))
                            ApplyPreview(hexBuffer);
                        continue;
                    }
                    if (key.Key == ConsoleKey.Enter)
                    {
                        if (Theme.TryParseHexColor(hexBuffer, out _, out _, out _))
                            return NormalizeHex(hexBuffer);
                        // Invalid — flash and stay in hex mode
                        hexInputMode = false;
                        hexBuffer = "#";
                        Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                        ApplyPreview(cells[cursor].Hex);
                        continue;
                    }
                    // Accept hex chars
                    var c = key.KeyChar;
                    if (hexBuffer.Length < 7 && "0123456789abcdefABCDEF".Contains(c))
                    {
                        hexBuffer += c;
                        Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                        if ((hexBuffer.Length == 4 || hexBuffer.Length == 7) &&
                            Theme.TryParseHexColor(hexBuffer, out _, out _, out _))
                            ApplyPreview(hexBuffer);
                    }
                    continue;
                }

                // Normal navigation mode
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                    case ConsoleKey.Q:
                        // Restore original
                        if (!string.IsNullOrEmpty(originalBg))
                            Theme.SetBackground(originalBg);
                        else
                            Theme.ResetBackground();
                        return null;

                    case ConsoleKey.Enter:
                        return cells[cursor].Hex;

                    case ConsoleKey.RightArrow:
                    case ConsoleKey.L:
                        if (cursor < cells.Count - 1)
                        {
                            cursor++;
                            Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                            ApplyPreview(cells[cursor].Hex);
                        }
                        break;

                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.H:
                        if (cursor > 0)
                        {
                            cursor--;
                            Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                            ApplyPreview(cells[cursor].Hex);
                        }
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        {
                            var newCursor = cursor + Columns;
                            if (newCursor >= cells.Count) newCursor = cells.Count - 1;
                            if (newCursor != cursor)
                            {
                                cursor = newCursor;
                                Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                                ApplyPreview(cells[cursor].Hex);
                            }
                        }
                        break;

                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        {
                            var newCursor = cursor - Columns;
                            if (newCursor < 0) newCursor = 0;
                            if (newCursor != cursor)
                            {
                                cursor = newCursor;
                                Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                                ApplyPreview(cells[cursor].Hex);
                            }
                        }
                        break;

                    default:
                        if (key.KeyChar == '#')
                        {
                            hexInputMode = true;
                            hexBuffer = "#";
                            Render(cells, sectionStarts, cursor, hexInputMode, hexBuffer);
                        }
                        break;
                }
            }
        }
        finally
        {
            // Clear picker display
            ClearRender(cells, sectionStarts);
            try { Console.CursorVisible = originalCursorVisible; } catch { }
        }
    }

    // ── Rendering ───────────────────────────────────────────────────

    private static int _renderStartRow;
    private static int _renderLineCount;

    private static void Render(List<PaletteEntry> cells, List<(int CellIndex, string Label)> sections,
        int cursor, bool hexInputMode, string hexBuffer)
    {
        // Position at start
        try
        {
            if (_renderLineCount == 0)
                _renderStartRow = Console.CursorTop;
            Console.SetCursorPosition(0, _renderStartRow);
        }
        catch { }

        var lines = new List<string>();
        lines.Add("");
        lines.Add("  \x1b[1mBackground Color Selector\x1b[0m");
        lines.Add("");

        int cellIdx = 0;
        int sectionIdx = 0;

        while (cellIdx < cells.Count)
        {
            // Section header
            if (sectionIdx < sections.Count && sections[sectionIdx].CellIndex == cellIdx)
            {
                lines.Add($"  \x1b[4m{sections[sectionIdx].Label}\x1b[0m");
                sectionIdx++;
            }

            // Row of swatches
            var line = "  ";
            var rowEnd = Math.Min(cellIdx + Columns, cells.Count);
            // Don't go past next section start
            if (sectionIdx < sections.Count)
                rowEnd = Math.Min(rowEnd, sections[sectionIdx].CellIndex);

            for (int i = cellIdx; i < rowEnd; i++)
            {
                var (r8, g8, b8) = ParseHexTo8Bit(cells[i].Hex);
                if (i == cursor)
                    line += $"\x1b[48;2;{r8};{g8};{b8}m\x1b[97m▐\x1b[90m██\x1b[97m▌\x1b[0m";
                else
                    line += $"\x1b[48;2;{r8};{g8};{b8}m    \x1b[0m";
            }
            lines.Add(line);
            cellIdx = rowEnd;
        }

        lines.Add("");

        // Status line
        if (hexInputMode)
        {
            var valid = Theme.TryParseHexColor(hexBuffer, out _, out _, out _);
            var indicator = valid ? "\x1b[32m✓\x1b[0m" : "\x1b[90m…\x1b[0m";
            lines.Add($"  Hex: {hexBuffer}█ {indicator}   (ESC to cancel, ENTER to apply)");
        }
        else
        {
            var entry = cells[cursor];
            lines.Add($"  \x1b[1m{entry.Hex}\x1b[0m  {entry.Name}");
        }

        lines.Add("  \x1b[90m←→↑↓ navigate  ENTER select  ESC cancel  # hex input\x1b[0m");
        lines.Add("");

        // Write all lines, clearing to end of each line
        foreach (var line in lines)
            Console.Write($"\x1b[2K{line}\n");

        _renderLineCount = lines.Count;
    }

    private static void ClearRender(List<PaletteEntry> cells, List<(int CellIndex, string Label)> sections)
    {
        try
        {
            Console.SetCursorPosition(0, _renderStartRow);
            for (int i = 0; i < _renderLineCount; i++)
                Console.Write("\x1b[2K\n");
            Console.SetCursorPosition(0, _renderStartRow);
        }
        catch { }
    }

    private static void ApplyPreview(string hex)
    {
        Theme.SetBackground(hex);
    }

    // ── Similar color generation ────────────────────────────────────

    internal static PaletteEntry[] GenerateSimilarColors(string hexColor, int count)
    {
        if (!Theme.TryParseHexColor(hexColor, out var r16, out var g16, out var b16))
            return Array.Empty<PaletteEntry>();

        var (h, s, l) = Theme.RgbToHsl(r16 / 65535.0, g16 / 65535.0, b16 / 65535.0);

        var results = new List<PaletteEntry>();
        var seen = new HashSet<string>();
        var inputNorm = hexColor.Trim().ToUpperInvariant();
        if (!inputNorm.StartsWith('#')) inputNorm = "#" + inputNorm;

        // Generate variations that are visibly different:
        // Wide hue spread (every 30°), with lightness and saturation shifts
        double[] hueOffsets = { -60, -30, 0, 30, 60, 90, 120, 150, 180 };
        // Keep lightness in same ballpark (dark stays dark, light stays light)
        double[] lightnessOffsets = { -0.08, 0, 0.08 };
        // Boost or reduce saturation noticeably
        double[] satOffsets = { -0.2, 0, 0.25 };

        foreach (var dh in hueOffsets)
        {
            if (dh == 0) continue; // skip same-hue, those look too similar
            foreach (var dl in lightnessOffsets)
            {
                var nh = (h + dh + 360) % 360;
                var ns = Math.Clamp(s + satOffsets[results.Count % satOffsets.Length], 0.15, 1.0);
                var nl = Math.Clamp(l + dl, 0.08, 0.92);

                var (nr, ng, nb) = Theme.HslToRgb(nh, ns, nl);
                var hex = $"#{(int)(nr * 255):X2}{(int)(ng * 255):X2}{(int)(nb * 255):X2}";

                if (seen.Add(hex) && hex != inputNorm)
                {
                    var hName = nh switch
                    {
                        >= 0 and < 30 => "Red",
                        >= 30 and < 60 => "Orange",
                        >= 60 and < 90 => "Yellow",
                        >= 90 and < 150 => "Green",
                        >= 150 and < 210 => "Cyan",
                        >= 210 and < 270 => "Blue",
                        >= 270 and < 330 => "Purple",
                        _ => "Red"
                    };
                    var lName = nl < 0.3 ? "Dark" : nl < 0.6 ? "Mid" : "Light";
                    results.Add(new PaletteEntry(hex, $"{lName} {hName}"));
                }

                if (results.Count >= count)
                    return results.ToArray();
            }
        }

        return results.ToArray();
    }

    // ── Utilities ───────────────────────────────────────────────────

    private static int FindClosestEntry(List<PaletteEntry> cells, string targetHex)
    {
        if (!Theme.TryParseHexColor(targetHex, out var tr, out var tg, out var tb))
            return 0;

        double minDist = double.MaxValue;
        int best = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            if (!Theme.TryParseHexColor(cells[i].Hex, out var cr, out var cg, out var cb))
                continue;
            var dr = (tr - cr) / 65535.0;
            var dg = (tg - cg) / 65535.0;
            var db = (tb - cb) / 65535.0;
            var dist = dr * dr + dg * dg + db * db;
            if (dist < minDist)
            {
                minDist = dist;
                best = i;
            }
        }
        return best;
    }

    private static (int r, int g, int b) ParseHexTo8Bit(string hex)
    {
        if (Theme.TryParseHexColor(hex, out var r16, out var g16, out var b16))
            return (r16 / 257, g16 / 257, b16 / 257); // 16-bit → 8-bit
        return (0, 0, 0);
    }

    internal static string NormalizeHex(string hex)
    {
        if (Theme.TryParseHexColor(hex, out var r16, out var g16, out var b16))
            return $"#{r16 / 257:X2}{g16 / 257:X2}{b16 / 257:X2}";
        return hex;
    }
}
