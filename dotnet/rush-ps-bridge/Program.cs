// rush-ps-bridge — standalone PowerShell bridge binary (#267).
//
// Entry point. Two modes, picked at the command line:
//
//   rush-ps-bridge --bridge   plugin JSON-lines protocol (Rush's plugin.ps wire)
//   rush-ps-bridge --mcp      MCP server (JSON-RPC 2.0 over stdio)
//
// Both modes share a PsRunner that owns a persistent PowerShell Runspace.
// Variables / function definitions set by one script persist to the next,
// matching the existing plugin protocol's "session" semantics.

using System.Reflection;

namespace Rush.PsBridge;

internal static class Program
{
    private static int Main(string[] args)
    {
        // --version / -v → print version and exit
        if (args.Any(a => a == "--version" || a == "-v"))
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";
            Console.WriteLine($"rush-ps-bridge {version}");
            return 0;
        }

        // --help / -h / no args → usage
        if (args.Length == 0 || args.Any(a => a == "--help" || a == "-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        // Shared PowerShell runner. Lazy-initialized so `--help` /
        // `--version` don't pay the SDK startup cost.
        using var runner = new PsRunner();

        return args[0] switch
        {
            "--bridge" => BridgeMode.Run(runner),
            "--mcp"    => McpMode.Run(runner),
            _ => Unknown(args[0]),
        };
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            rush-ps-bridge — PowerShell bridge for Rush and MCP clients

            USAGE
              rush-ps-bridge --bridge       plugin JSON-lines protocol (for Rush's
                                            `plugin.ps ... end` blocks)
              rush-ps-bridge --mcp          MCP server (JSON-RPC 2.0 over stdio,
                                            for `mcp("ps", ...)` and any MCP client)
              rush-ps-bridge --version      print version and exit
              rush-ps-bridge --help         this message

            Both modes speak JSON over stdin/stdout. Stderr carries diagnostic
            logs. A persistent PowerShell runspace is created on first use and
            reused for subsequent calls in the same invocation.
            """);
    }

    private static int Unknown(string arg)
    {
        Console.Error.WriteLine($"rush-ps-bridge: unknown argument: {arg}");
        Console.Error.WriteLine("Run with --help for usage.");
        return 2;
    }
}
