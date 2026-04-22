// --bridge mode — plugin JSON-lines protocol.
//
// Phase 1 scaffold: prints a "not yet implemented" message and exits
// so the binary has a defined shape from the start. Phase 2 will
// implement the actual JSON-lines loop matching Rush's plugin.ps
// wire protocol.

namespace Rush.PsBridge;

internal static class BridgeMode
{
    public static int Run(PsRunner runner)
    {
        _ = runner; // Phase 2 will use this.
        Console.Error.WriteLine(
            "rush-ps-bridge --bridge: plugin protocol not yet implemented (Phase 2 of #267). "
            + "Use --help for available modes.");
        return 64; // EX_USAGE
    }
}
