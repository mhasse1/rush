# Status Line Design

## Goal

A persistent status line at the **top** of the terminal that shows contextual information — hints, mode indicators, background job counts, and whatever else we want in the future. Replaces the broken floating hint approach.

## Design Priorities

1. **Reliability** — If the status line disappears (terminal reset, external program, resize), repaint it. Never leave the terminal in a broken state.
2. **Ease** — Simple implementation that doesn't fight the terminal. Degrade gracefully.

## Philosophy: Use What the Terminal Gives Us

Don't fight the terminal. Instead:

- If the terminal offers a native status bar API, use it.
- If we're in iTerm or another feature-rich terminal, take advantage of its specific capabilities (e.g., iTerm status bar items via escape sequences).
- Offer hooks so users can wire up their own integrations (e.g., `rush_statusline()` callback that returns content, let the user decide where it goes).
- If the terminal gives us nothing, **skip it** — don't force a scroll-region hack that's fragile.

The ANSI scroll region approach below is a fallback for terminals that don't offer anything better, not the primary strategy.

## Architecture

### ANSI Scroll Region (Fallback)

```
Row 1:  [ status line — pinned, outside scroll region ]
Row 2–N: [ normal shell output — scrolls normally ]
```

Set scroll region on startup and after resize:
```
\x1b[2;{rows}r     # scroll region = rows 2 to N
\x1b[2;1H           # move cursor to row 2, col 1
```

### Updating the Status Line

```
\x1b[s              # save cursor position
\x1b[1;1H           # move to row 1, col 1
\x1b[2K             # clear entire line
{write status content}
\x1b[u              # restore cursor position
```

Or use `Console.SetCursorPosition()` if ANSI codes prove unreliable (as we learned with the floating hint).

### When to Update

- On every prompt render (before showing the prompt)
- After `clear` command (repaint the status line)
- After `SIGWINCH` (terminal resize) — recalculate rows, reset scroll region
- After any external command finishes (it may have reset scroll regions)

### Content

Phase 1 (MVP):
```
 vi:insert | esc v → $EDITOR                          rush 1.2.52
```

Phase 2 (future):
```
 vi:normal | 3 jobs | esc v → $EDITOR                 ~/src/rush main*
```

Content varies by context:
- **Normal prompt**: mode indicator, version
- **Multi-line input**: mode + edit-in-editor hint
- **Running command**: blank or "running..."
- **Background jobs**: job count when > 0

### Terminal Resize (SIGWINCH)

```csharp
Console.CancelKeyPress += ...;  // already handled
// Add: PosixSignalRegistration for SIGWINCH
PosixSignalRegistration.Create(PosixSignal.SIGWINCH, ctx => {
    var rows = Console.WindowHeight;
    // Reset scroll region
    Console.Write($"\x1b[2;{rows}r");
    // Repaint status line
    RepaintStatusLine();
});
```

### Graceful Degradation

- If terminal doesn't support scroll regions (rare), skip status line entirely
- If status line gets overwritten by external program, repaint on next prompt
- `clear` command: clear scroll region content + repaint status line
- `less`/`vim` (alternate screen): they save/restore the screen, so status line comes back automatically

### Impact on Existing Code

- `Console.Clear()` handler needs to repaint status line after clearing
- `Prompt.Render()` should call `RepaintStatusLine()` before rendering
- `ShowHelp()` output scrolls normally (within the scroll region)
- External commands: scroll region persists across child processes

### Key Risk: External Commands Resetting Scroll Regions

Some programs (vim, less, htop) switch to alternate screen buffer (`\x1b[?1049h`), which saves and restores the main screen including scroll regions. These are safe.

Programs that write raw ANSI escape codes could theoretically reset the scroll region. Mitigation: repaint on every prompt (after command finishes).

### Files to Modify

| File | Changes |
|------|---------|
| `Program.cs` | `StatusLine` class, scroll region setup, resize handler, repaint calls |
| `Prompt.cs` | Call `StatusLine.Repaint()` before rendering prompt |
| `Program.cs` | `clear` handler → repaint after clear |
| `LineEditor.cs` | Maybe — cursor row calculations may need +1 offset for status line |

### Terminal-Specific Integrations

Before falling back to scroll regions, detect what the terminal offers:

| Terminal | Capability | How |
|----------|-----------|-----|
| iTerm2 | Status bar items | Custom escape sequences (`\x1b]1337;SetBadgeFormat=...`) |
| kitty | Protocol extensions | Kitty graphics/status protocol |
| WezTerm | Multiplexer-aware | Custom escape sequences |
| Generic | Nothing native | Fall back to ANSI scroll region or skip |

Detection: check `$TERM_PROGRAM`, `$ITERM_SESSION_ID`, `$KITTY_PID`, etc.

### User Hooks

Provide a `rush_statusline()` function that rush calls on each prompt. The user returns a string (or empty to disable). This lets users wire the content to whatever display mechanism their terminal supports — or pipe it to tmux status, etc.

```rush
fn rush_statusline()
  return "vi:#{vi_mode} | #{cwd.short}"
end
```

### Estimated Scope

~150-200 lines of new code, plus ~20 lines of modifications to existing code.
