using System.Text.Json.Nodes;

namespace AntlrCompiler.Cli.Lsp;

/// <summary>
/// Minimal LSP protocol type helpers.
/// Uses System.Text.Json.Nodes for dynamic JSON construction.
/// </summary>
internal static class LspTypes
{
    /// <summary>Creates a Position object { line, character }.</summary>
    public static JsonObject Position(int line, int character) => new()
    {
        ["line"] = line,
        ["character"] = character
    };

    /// <summary>Creates a Range object { start, end }.</summary>
    public static JsonObject Range(int startLine, int startChar, int endLine, int endChar) => new()
    {
        ["start"] = Position(startLine, startChar),
        ["end"] = Position(endLine, endChar)
    };

    /// <summary>Creates a Location object { uri, range }.</summary>
    public static JsonObject Location(string uri, int startLine, int startChar, int endLine, int endChar) => new()
    {
        ["uri"] = uri,
        ["range"] = Range(startLine, startChar, endLine, endChar)
    };

    /// <summary>Creates an LSP Diagnostic.</summary>
    public static JsonObject Diagnostic(int startLine, int startChar, int endLine, int endChar,
        int severity, string message, string? code = null)
    {
        var diag = new JsonObject
        {
            ["range"] = Range(startLine, startChar, endLine, endChar),
            ["severity"] = severity,
            ["source"] = "aura",
            ["message"] = message
        };
        if (code is not null)
            diag["code"] = code;
        return diag;
    }

    /// <summary>Creates a CompletionItem.</summary>
    public static JsonObject CompletionItem(string label, int kind, string? detail = null)
    {
        var item = new JsonObject
        {
            ["label"] = label,
            ["kind"] = kind
        };
        if (detail is not null)
            item["detail"] = detail;
        return item;
    }

    /// <summary>Creates a Hover result.</summary>
    public static JsonObject Hover(string contents, int startLine, int startChar, int endLine, int endChar) => new()
    {
        ["contents"] = new JsonObject
        {
            ["kind"] = "markdown",
            ["value"] = contents
        },
        ["range"] = Range(startLine, startChar, endLine, endChar)
    };

    // LSP Severity constants
    public const int SeverityError = 1;
    public const int SeverityWarning = 2;
    public const int SeverityInformation = 3;
    public const int SeverityHint = 4;

    // LSP CompletionItemKind constants
    public const int KindFunction = 3;
    public const int KindField = 5;
    public const int KindVariable = 6;
    public const int KindClass = 7;
    public const int KindInterface = 8;
    public const int KindModule = 9;
    public const int KindProperty = 10;
    public const int KindEnum = 13;
    public const int KindKeyword = 14;
    public const int KindSnippet = 15;
    public const int KindStruct = 22;
}
