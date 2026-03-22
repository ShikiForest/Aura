using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuraLang.Cli.Lsp;

/// <summary>
/// Minimal JSON-RPC 2.0 transport over stdio for LSP.
/// Reads/writes "Content-Length: N\r\n\r\n{json}" framed messages.
/// </summary>
internal static class JsonRpc
{
    /// <summary>Reads a single JSON-RPC message from stdin. Returns null on EOF.</summary>
    public static JsonObject? ReadMessage(Stream input)
    {
        // Read headers
        int contentLength = -1;
        while (true)
        {
            var line = ReadLine(input);
            if (line is null) return null; // EOF

            if (line.Length == 0) break; // empty line = end of headers

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.Substring("Content-Length:".Length).Trim();
                contentLength = int.Parse(value);
            }
            // Ignore other headers (Content-Type, etc.)
        }

        if (contentLength < 0) return null;

        // Read body
        var buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = input.Read(buffer, read, contentLength - read);
            if (n == 0) return null; // EOF
            read += n;
        }

        var json = Encoding.UTF8.GetString(buffer);
        return JsonNode.Parse(json)?.AsObject();
    }

    /// <summary>Writes a JSON-RPC message to stdout.</summary>
    public static void WriteMessage(Stream output, JsonObject message)
    {
        var json = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        output.Write(header);
        output.Write(body);
        output.Flush();
    }

    /// <summary>Sends a JSON-RPC response for a request.</summary>
    public static void SendResponse(Stream output, JsonNode? id, JsonNode? result)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result?.DeepClone()
        };
        WriteMessage(output, msg);
    }

    /// <summary>Sends a JSON-RPC error response.</summary>
    public static void SendError(Stream output, JsonNode? id, int code, string message)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        WriteMessage(output, msg);
    }

    // Standard JSON-RPC error codes
    public const int MethodNotFound = -32601;

    /// <summary>Sends a JSON-RPC notification (no id).</summary>
    public static void SendNotification(Stream output, string method, JsonNode? @params)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params?.DeepClone()
        };
        WriteMessage(output, msg);
    }

    // Read a line terminated by \r\n from the stream
    private static string? ReadLine(Stream stream)
    {
        var sb = new StringBuilder();
        int prev = -1;
        while (true)
        {
            int b = stream.ReadByte();
            if (b == -1) return sb.Length > 0 ? sb.ToString() : null;
            if (b == '\n' && prev == '\r')
            {
                // Remove the trailing \r
                if (sb.Length > 0 && sb[^1] == '\r')
                    sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)b);
            prev = b;
        }
    }
}
