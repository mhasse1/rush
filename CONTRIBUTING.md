# Contributing to Rush

Thanks for your interest in Rush. Here's how to get started.

## Quick Start

```bash
# Clone and build
git clone https://github.com/mhasse1/rush.git
cd rush
dotnet build

# Run tests
dotnet test Rush.Tests

# Run your build
bin/Debug/net10.0/osx-arm64/rush    # adjust RID for your platform
```

Requires .NET 10 SDK. Install via [dotnet.microsoft.com](https://dotnet.microsoft.com/download) or `brew install dotnet`.

## Development Workflow

```bash
# Build and install (macOS — creates symlink, auto-updates on rebuild)
./install.sh

# After install.sh, every publish updates the installed binary:
dotnet publish -c Release -r osx-arm64

# Run the full test suite
dotnet test Rush.Tests
```

## Project Structure

```
Program.cs           Main entry, REPL loop, builtin commands (~5K lines)
Lexer.cs             Tokenizer
Parser.cs            Recursive descent parser → AST
Transpiler.cs        AST → PowerShell 7 code generation
ScriptEngine.cs      Triage (Rush syntax vs shell command), block detection
LineEditor.cs        Vi/emacs line editing, history, tab completion
CommandTranslator.cs Pipeline operators, after-pipe translations
TabCompleter.cs      Path, command, flag, and pipeline op completion
HelpCommand.cs       Help system (embedded YAML)
HelpRenderer.cs      REPL-formatted help output
TrainingHints.cs     Bash-to-Rush pattern hints
Theme.cs             Color detection, contrast validation, theming
Config.cs            Settings, aliases, startup scripts
LlmMode.cs           rush --llm JSON wire protocol
McpLocalMode.cs      rush --mcp (local MCP server)
McpSshMode.cs        rush --mcp-ssh (SSH gateway MCP server)
SshLlmSession.cs     Persistent SSH sessions for MCP
AiCommand.cs         ai "prompt" — built-in AI assistant

docs/
  rush-help.yaml       Embedded help topics (22 topics)
  rush-lang-spec.yaml  Compact language spec (embedded, ~300 lines)
  user-manual.md       Comprehensive user documentation
  rush-features.md     Feature reference

Rush.Tests/            xUnit test suite (1113+ tests)
```

## Adding a New Feature

Most features touch 4-6 files in a predictable pattern:

**New keyword or block syntax:**
1. `Lexer.cs` — add token type and keyword mapping
2. `Parser.cs` — add parsing method
3. `ScriptEngine.cs` — add to `BlockStartKeywords`, `RushKeywords`, `GetBlockDepth`
4. `Transpiler.cs` — add transpilation
5. `SyntaxHighlighter.cs` — add to keyword set
6. Tests in `Rush.Tests/`

**New builtin command:**
1. `Program.cs` — add to `RushConstants.Builtins` and dispatch in `ProcessCommand`
2. Tests

**New help topic:**
1. `docs/rush-help.yaml` — add topic block
2. `HelpRenderer.cs` — add to category list
3. `HelpCommand.cs` — add to category list
4. `Program.cs` — add `--help` keyword mapping if needed

## Code Style

- C# with .NET 10, implicit usings enabled
- No unnecessary abstractions — prefer direct code over patterns
- Comments explain *why*, not *what*
- Keep changes focused — one feature or fix per PR
- Don't add features, refactor code, or "improve" things beyond what's needed

## Testing

```bash
# Full suite
dotnet test Rush.Tests

# Single test
dotnet test Rush.Tests --filter "TestMethodName"

# Tests that shell out use TestHelper.RunRush() — needs a built binary
dotnet build && dotnet test Rush.Tests
```

Tests run on macOS, Linux, and Windows via CI. Platform-specific tests are guarded:
```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
```

## Submitting Changes

1. Fork the repo
2. Create a branch (`git checkout -b my-feature`)
3. Make your changes
4. Run the full test suite
5. Commit with a clear message
6. Open a PR against `main`

CI runs automatically on PRs — all three platforms must pass.

## Reporting Bugs

[Open an issue](https://github.com/mhasse1/rush/issues/new) with:
- What you did
- What you expected
- What happened instead
- Platform and Rush version (`rush --version`)

## License

By contributing, you agree that your contributions will be licensed under the project's [BSL 1.1 license](LICENSE).
