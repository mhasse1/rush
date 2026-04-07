# Rabbit Hole: Rush Architecture Evolution

**Status:** Internal design discussion. Not public.
**Date:** 2026-04-06

---

## The Core Insight

The JSON envelope protocol we built for SSH transport is the same interface needed between a native Rush core and a PowerShell plugin. We accidentally designed the plugin boundary while solving a different problem.

## Current Architecture

```
Rush syntax → Lexer → Parser → AST → Transpiler → PowerShell string → PS runtime executes everything
```

Everything runs through PowerShell. The PS runtime is the engine. Rush is a transpiler that generates PS code.

## Proposed Architecture

```
Rush syntax → Lexer → Parser → AST → Native core executes
                                         ↓
                                    ps...end blocks route to PS plugin
                                         ↓ JSON envelope
                                    PS7 runtime (plugin)
                                         ↓ typed objects / JSON result
                                    Native core
```

### What the native core handles (Rust):
- Variables, assignment, interpolation
- Control flow (if/unless/for/while/case)
- Functions, classes, enums
- File/Dir/Time stdlib (native I/O, no PS)
- Process execution (native fork/exec)
- Pipes, redirections, globs
- REPL, line editor, tab completion, history
- MCP servers (local + SSH)
- LLM mode wire protocol

### What the PS plugin handles:
- `ps...end` blocks (PS7, cross-platform)
- `ps5...end` blocks (PS 5.1, Windows only)
- `win32...end` blocks (32-bit PS, Windows only)
- Windows cmdlets (AD, Exchange, Azure, IIS, etc.)
- .NET method calls when explicitly requested
- Object-mode pipeline output (typed JSON)

### The plugin boundary:
- JSON envelope in, JSON result out (same as --llm wire protocol)
- Variable bridging via JSON serialization (same as ps5 bridge)
- Plugin loaded on demand, not at startup
- Optional — Rush works without PS for pure Unix workloads

## Why PS7 as a Plugin is More Powerful

PS7 runs on macOS, Linux, and Windows. The plugin isn't Windows-only:

```rush
# On macOS, managing Windows AD directly:
ps
  Import-Module ActiveDirectory
  Get-ADUser -Filter * -Server cor1s02.domain.local
end
```

No SSH hop needed for PS execution. PS7 connects to remote Windows services natively (ADWS, WMI, WinRM). Rush on macOS with the PS plugin can manage Windows infrastructure directly.

Current path: macOS → SSH → Windows → Rush → PS → AD (5 hops)
Plugin path: macOS → Rush → PS plugin → AD via WinRM (3 hops)

## Why This Works Now

Before this week, the boundary between Rush and PS was blurry — every expression was a PS string. Now we have:

1. **JSON envelope protocol** — clean input/output contracts, tested across 4 hosts
2. **Structured results** — status, stdout, stderr, exit_code, objects, mime, duration
3. **Variable bridging** — JSON serialization of Rush vars into PS context (ps5 bridge)
4. **InjectPsHelpers pattern** — shows how to inject PS capabilities into a runtime
5. **Metacharacter safety** — JSON transport eliminates all escaping issues
6. **6,000+ test assertions** proving the protocol works

## Benefits

### Binary size
- Current: ~120MB (bundles entire .NET runtime)
- Future: ~5-10MB Rust core + optional ~50MB PS plugin download

### Startup time
- Current: ~800ms (JIT + PS runspace initialization)
- Future: ~10ms native core, PS plugin lazy-loaded on first `ps` block

### Container story
- Current: requires .NET runtime in container
- Future: 5MB static binary, `FROM scratch` compatible

### Dependency
- Current: .NET 10 SDK to build, .NET runtime to run
- Future: zero dependencies for core, PS7 optional

### CI stability
- Current: .NET SDK version mismatches cause binary crashes
- Future: Rust produces static binaries, no runtime version issues

## Sequencing Options

### Option A: Ship .NET, rewrite later
- Ship current architecture for beta
- Prove market with Windows admin story
- Rewrite core to Rust post-1.0
- Risk: rewrite is a multi-month project, may lose momentum

### Option B: Rust core now, PS plugin from day one
- Longer time to beta
- But: smaller binary, faster startup, no .NET dependency
- The PS plugin interface is already designed (JSON envelope)
- Risk: delays launch

### Option C: Hybrid migration
- Start migrating individual subsystems to Rust
- Keep PS runtime for execution but move REPL, parser, MCP to Rust
- Gradual, testable, no big bang
- Risk: maintaining two codepaths during migration

## Recommendation

Option A for immediate launch. The .NET engine works, is tested, and the Windows admin story is the differentiator. The plugin architecture is the clear evolution path — we've already designed the interface. File it, ship beta, then execute the migration with revenue/users validating the direction.

## Related Issues

- #138 AI agent with LLM mode subshell
- #151 Orchestration mode
- #162 Self-hosting (rewrite bash scripts in Rush)
- #163 Zero bash/zsh dependency
- #104 JSON IR as first-class representation
