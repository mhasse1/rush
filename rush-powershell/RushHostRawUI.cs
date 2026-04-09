using System.Management.Automation.Host;

namespace Rush;

/// <summary>
/// Minimal PSHostRawUserInterface implementation.
/// Provides console dimensions and basic cursor info to PowerShell.
/// </summary>
public class RushHostRawUI : PSHostRawUserInterface
{
    public override ConsoleColor ForegroundColor
    {
        get => Console.ForegroundColor;
        set => Console.ForegroundColor = value;
    }

    public override ConsoleColor BackgroundColor
    {
        get => Console.BackgroundColor;
        set => Console.BackgroundColor = value;
    }

    public override Coordinates CursorPosition
    {
        get => new(Console.CursorLeft, Console.CursorTop);
        set
        {
            Console.CursorLeft = value.X;
            Console.CursorTop = value.Y;
        }
    }

    public override Coordinates WindowPosition
    {
        get => new(0, 0);
        set { }
    }

    public override int CursorSize
    {
        get => 25;
        set { }
    }

    public override Size BufferSize
    {
        get
        {
            try { return new Size(Console.BufferWidth, Console.BufferHeight); }
            catch { return new Size(120, 50); }
        }
        set { }
    }

    public override Size WindowSize
    {
        get
        {
            try { return new Size(Console.WindowWidth, Console.WindowHeight); }
            catch { return new Size(120, 50); }
        }
        set { }
    }

    public override Size MaxWindowSize => WindowSize;
    public override Size MaxPhysicalWindowSize => WindowSize;

    public override bool KeyAvailable => Console.KeyAvailable;

    public override string WindowTitle
    {
        get
        {
            if (OperatingSystem.IsWindows()) return Console.Title;
            return "rush";
        }
        set
        {
            if (OperatingSystem.IsWindows()) Console.Title = value;
        }
    }

    public override KeyInfo ReadKey(ReadKeyOptions options)
    {
        var intercept = (options & ReadKeyOptions.NoEcho) != 0;
        var key = Console.ReadKey(intercept);
        return new KeyInfo(
            (int)key.Key,
            key.KeyChar,
            KeyToControlKeyState(key.Modifiers),
            key.Key != 0);
    }

    public override void FlushInputBuffer() { }

    public override BufferCell[,] GetBufferContents(Rectangle rectangle)
    {
        throw new NotImplementedException();
    }

    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill)
    {
    }

    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
    public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }

    private static ControlKeyStates KeyToControlKeyState(ConsoleModifiers modifiers)
    {
        var state = (ControlKeyStates)0;
        if ((modifiers & ConsoleModifiers.Alt) != 0) state |= ControlKeyStates.LeftAltPressed;
        if ((modifiers & ConsoleModifiers.Control) != 0) state |= ControlKeyStates.LeftCtrlPressed;
        if ((modifiers & ConsoleModifiers.Shift) != 0) state |= ControlKeyStates.ShiftPressed;
        return state;
    }
}
