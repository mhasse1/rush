// --mcp mode — MCP JSON-RPC 2.0 server on stdio.
//
// Phase 1 scaffold: prints a "not yet implemented" message and exits.
// Phase 3 will implement the initialize handshake, tools/list (exposing
// `invoke`), and tools/call routing into PsRunner.

namespace Rush.PsBridge;

internal static class McpMode
{
    public static int Run(PsRunner runner)
    {
        _ = runner; // Phase 3 will use this.
        Console.Error.WriteLine(
            "rush-ps-bridge --mcp: MCP server mode not yet implemented (Phase 3 of #267). "
            + "Use --help for available modes.");
        return 64; // EX_USAGE
    }
}
