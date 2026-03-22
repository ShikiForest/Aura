using System.Text.Json.Nodes;
using AuraLang.Cli.Lsp;

namespace AuraLang.Cli;

/// <summary>
/// CLI entry point for the Aura Language Server.
/// Reads JSON-RPC messages from stdin, dispatches to LspServer, and writes responses to stdout.
/// </summary>
internal static class LspCommand
{
    public static int Execute()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        var server = new LspServer(stdout);

        while (true)
        {
            JsonObject? message;
            try
            {
                message = JsonRpc.ReadMessage(stdin);
            }
            catch (EndOfStreamException)
            {
                break; // Client disconnected
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[aura-lsp] Read error: {ex.Message}");
                continue;
            }

            if (message is null)
                break;

            try
            {
                server.HandleMessage(message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[aura-lsp] Handler error: {ex.Message}");
            }

            // exit notification was handled inside HandleMessage via Environment.Exit
        }

        return server.ShutdownRequested ? 0 : 1;
    }
}
