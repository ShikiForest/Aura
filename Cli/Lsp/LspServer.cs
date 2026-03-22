using System.Text.Json.Nodes;
using AuraLang.Ast;
using AuraLang.Semantics;

namespace AuraLang.Cli.Lsp;

/// <summary>
/// Aura Language Server — handles LSP requests and notifications.
/// Manages open documents, runs parse + semantic analysis, and provides
/// diagnostics, completion, hover, and go-to-definition.
/// </summary>
internal sealed class LspServer
{
    private readonly Stream _output;
    private readonly Dictionary<string, DocumentState> _documents = new();
    private bool _shutdownRequested;

    public bool ShutdownRequested => _shutdownRequested;

    public LspServer(Stream output)
    {
        _output = output;
    }

    // ── Request/Notification dispatch ────────────────────────────────────

    public void HandleMessage(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var id = message["id"];
        var @params = message["params"]?.AsObject();

        switch (method)
        {
            case "initialize":
                HandleInitialize(id);
                break;
            case "initialized":
                break; // no-op
            case "shutdown":
                _shutdownRequested = true;
                JsonRpc.SendResponse(_output, id, null);
                break;
            case "exit":
                Environment.Exit(_shutdownRequested ? 0 : 1);
                break;
            case "textDocument/didOpen":
                HandleDidOpen(@params);
                break;
            case "textDocument/didChange":
                HandleDidChange(@params);
                break;
            case "textDocument/didClose":
                HandleDidClose(@params);
                break;
            case "textDocument/completion":
                HandleCompletion(id, @params);
                break;
            case "textDocument/hover":
                HandleHover(id, @params);
                break;
            case "textDocument/definition":
                HandleDefinition(id, @params);
                break;
            default:
                // Unknown method — return MethodNotFound for requests, ignore notifications
                if (id is not null)
                    JsonRpc.SendError(_output, id, JsonRpc.MethodNotFound,
                        $"Method not found: {method}");
                break;
        }
    }

    // ── initialize ───────────────────────────────────────────────────────

    private void HandleInitialize(JsonNode? id)
    {
        var capabilities = new JsonObject
        {
            ["textDocumentSync"] = new JsonObject
            {
                ["openClose"] = true,
                ["change"] = 1 // Full sync
            },
            ["completionProvider"] = new JsonObject
            {
                ["triggerCharacters"] = new JsonArray(".")
            },
            ["hoverProvider"] = true,
            ["definitionProvider"] = true
        };

        var result = new JsonObject
        {
            ["capabilities"] = capabilities,
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "Aura Language Server",
                ["version"] = "0.1.0"
            }
        };

        JsonRpc.SendResponse(_output, id, result);
    }

    // ── textDocument/didOpen ─────────────────────────────────────────────

    private void HandleDidOpen(JsonObject? @params)
    {
        var doc = @params?["textDocument"]?.AsObject();
        if (doc is null) return;

        var uri = doc["uri"]?.GetValue<string>() ?? "";
        var text = doc["text"]?.GetValue<string>() ?? "";

        _documents[uri] = new DocumentState(text, null, null);
        AnalyzeAndPublish(uri);
    }

    // ── textDocument/didChange ───────────────────────────────────────────

    private void HandleDidChange(JsonObject? @params)
    {
        var doc = @params?["textDocument"]?.AsObject();
        var changes = @params?["contentChanges"]?.AsArray();
        if (doc is null || changes is null) return;

        var uri = doc["uri"]?.GetValue<string>() ?? "";

        // Full sync: take the last change's text
        string? text = null;
        foreach (var change in changes)
        {
            text = change?["text"]?.GetValue<string>();
        }
        if (text is null) return;

        _documents[uri] = new DocumentState(text, null, null);
        AnalyzeAndPublish(uri);
    }

    // ── textDocument/didClose ────────────────────────────────────────────

    private void HandleDidClose(JsonObject? @params)
    {
        var doc = @params?["textDocument"]?.AsObject();
        if (doc is null) return;

        var uri = doc["uri"]?.GetValue<string>() ?? "";
        _documents.Remove(uri);

        // Clear diagnostics
        PublishDiagnostics(uri, new JsonArray());
    }

    // ── textDocument/completion ──────────────────────────────────────────

    private void HandleCompletion(JsonNode? id, JsonObject? @params)
    {
        var uri = @params?["textDocument"]?["uri"]?.GetValue<string>() ?? "";
        var items = new JsonArray();

        // Add keywords
        foreach (var kw in s_keywords)
            items.Add(LspTypes.CompletionItem(kw, LspTypes.KindKeyword));

        // Add built-in types
        foreach (var t in s_builtinTypes)
            items.Add(LspTypes.CompletionItem(t, LspTypes.KindKeyword, "built-in type"));

        // Add symbols from current document's AST
        if (_documents.TryGetValue(uri, out var state) && state.Ast is not null)
        {
            foreach (var item in state.Ast.Items)
            {
                switch (item)
                {
                    case FunctionDeclNode fn:
                        items.Add(LspTypes.CompletionItem(fn.Name.Text, LspTypes.KindFunction));
                        break;
                    case ClassDeclNode cls:
                        items.Add(LspTypes.CompletionItem(cls.Name.Text, LspTypes.KindClass));
                        break;
                    case StructDeclNode st:
                        items.Add(LspTypes.CompletionItem(st.Name.Text, LspTypes.KindStruct));
                        break;
                    case TraitDeclNode tr:
                        items.Add(LspTypes.CompletionItem(tr.Name.Text, LspTypes.KindInterface));
                        break;
                    case EnumDeclNode en:
                        items.Add(LspTypes.CompletionItem(en.Name.Text, LspTypes.KindEnum));
                        break;
                }
            }
        }

        JsonRpc.SendResponse(_output, id, items);
    }

    // ── textDocument/hover ───────────────────────────────────────────────

    private void HandleHover(JsonNode? id, JsonObject? @params)
    {
        var uri = @params?["textDocument"]?["uri"]?.GetValue<string>() ?? "";
        var pos = @params?["position"]?.AsObject();
        if (pos is null) { JsonRpc.SendResponse(_output, id, null); return; }

        int line = pos["line"]?.GetValue<int>() ?? 0;
        int col = pos["character"]?.GetValue<int>() ?? 0;

        if (!_documents.TryGetValue(uri, out var state) || state.Ast is null)
        {
            JsonRpc.SendResponse(_output, id, null);
            return;
        }

        // Find declaration at position
        var node = FindNodeAtPosition(state.Ast, line + 1, col); // LSP 0-based → ANTLR 1-based line
        if (node is null)
        {
            JsonRpc.SendResponse(_output, id, null);
            return;
        }

        var (info, span) = node switch
        {
            FunctionDeclNode fn => ($"```aura\nfn {fn.Name.Text}(...) -> {fn.ReturnSpec?.ToString() ?? "void"}\n```", fn.Span),
            ClassDeclNode cls => ($"```aura\nclass {cls.Name.Text}\n```", cls.Span),
            StructDeclNode st => ($"```aura\nstruct {st.Name.Text}\n```", st.Span),
            TraitDeclNode tr => ($"```aura\ntrait {tr.Name.Text}\n```", tr.Span),
            EnumDeclNode en => ($"```aura\nenum {en.Name.Text}\n```", en.Span),
            VarDeclStmtNode v => ($"```aura\n{(v.Mutability == Mutability.Let ? "let" : "var")} {v.Name.Text}\n```", v.Span),
            _ => ((string?)null, default(SourceSpan))
        };

        if (info is null)
        {
            JsonRpc.SendResponse(_output, id, null);
            return;
        }

        var hover = LspTypes.Hover(info,
            span.Start.Line - 1, span.Start.Column,
            span.End.Line - 1, span.End.Column);
        JsonRpc.SendResponse(_output, id, hover);
    }

    // ── textDocument/definition ──────────────────────────────────────────

    private void HandleDefinition(JsonNode? id, JsonObject? @params)
    {
        var uri = @params?["textDocument"]?["uri"]?.GetValue<string>() ?? "";
        var pos = @params?["position"]?.AsObject();
        if (pos is null) { JsonRpc.SendResponse(_output, id, null); return; }

        int line = pos["line"]?.GetValue<int>() ?? 0;
        int col = pos["character"]?.GetValue<int>() ?? 0;

        if (!_documents.TryGetValue(uri, out var state) || state.Ast is null)
        {
            JsonRpc.SendResponse(_output, id, null);
            return;
        }

        // Find declaration at position (same logic as hover)
        var node = FindNodeAtPosition(state.Ast, line + 1, col);
        if (node is null)
        {
            JsonRpc.SendResponse(_output, id, null);
            return;
        }

        var span = node.Span;
        var location = LspTypes.Location(uri,
            span.Start.Line - 1, span.Start.Column,
            span.End.Line - 1, span.End.Column);
        JsonRpc.SendResponse(_output, id, location);
    }

    // ── Analysis + diagnostics publishing ────────────────────────────────

    private void AnalyzeAndPublish(string uri)
    {
        if (!_documents.TryGetValue(uri, out var state)) return;

        var diagnostics = new JsonArray();
        CompilationUnitNode? ast = null;
        IReadOnlyList<Diagnostic>? parseDiags = null;

        try
        {
            // Parse
            var parseResult = AuraFrontEnd.ParseCompilationUnit(state.Text);
            parseDiags = parseResult.Diagnostics;

            foreach (var d in parseResult.Diagnostics)
            {
                diagnostics.Add(LspTypes.Diagnostic(
                    d.Span.Start.Line - 1, d.Span.Start.Column,
                    d.Span.End.Line - 1, d.Span.End.Column,
                    LspTypes.SeverityError, d.Message));
            }

            ast = parseResult.Ast;

            // Semantic analysis (only if parse succeeded)
            if (ast is not null)
            {
                var semResult = SemanticFrontEnd.Check(ast);
                foreach (var d in semResult.Diagnostics)
                {
                    int severity = d.Severity switch
                    {
                        DiagnosticSeverity.Error => LspTypes.SeverityError,
                        DiagnosticSeverity.Warning => LspTypes.SeverityWarning,
                        _ => LspTypes.SeverityInformation
                    };
                    diagnostics.Add(LspTypes.Diagnostic(
                        d.Span.Start.Line - 1, d.Span.Start.Column,
                        d.Span.End.Line - 1, d.Span.End.Column,
                        severity, d.Message, d.Code));
                }
            }
        }
        catch (Exception ex)
        {
            // Report internal error as a diagnostic so the user sees it
            diagnostics.Add(LspTypes.Diagnostic(0, 0, 0, 0,
                LspTypes.SeverityWarning, $"[aura-lsp] Analysis error: {ex.Message}"));
        }

        _documents[uri] = new DocumentState(state.Text, ast, parseDiags);
        PublishDiagnostics(uri, diagnostics);
    }

    private void PublishDiagnostics(string uri, JsonArray diagnostics)
    {
        var @params = new JsonObject
        {
            ["uri"] = uri,
            ["diagnostics"] = diagnostics
        };
        JsonRpc.SendNotification(_output, "textDocument/publishDiagnostics", @params);
    }

    // ── AST navigation helper ────────────────────────────────────────────

    private static SyntaxNode? FindNodeAtPosition(CompilationUnitNode ast, int line, int col)
    {
        // Simple linear scan for top-level declarations
        foreach (var item in ast.Items)
        {
            if (item is not SyntaxNode node) continue;
            if (!SpanContains(node.Span, line, col)) continue;

            // Check nested members for more specific match
            IEnumerable<object>? members = node switch
            {
                ClassDeclNode cls => cls.Members,
                StructDeclNode st => st.Members,
                TraitDeclNode tr => tr.Members,
                EnumDeclNode en => en.Members,
                WindowDeclNode wd => wd.Members,
                _ => null
            };

            if (members is not null)
            {
                foreach (var m in members)
                {
                    if (m is SyntaxNode mn && SpanContains(mn.Span, line, col))
                        return mn;
                }
            }

            return node;
        }
        return null;
    }

    private static bool SpanContains(SourceSpan span, int line, int col)
    {
        if (line < span.Start.Line || line > span.End.Line) return false;
        if (line == span.Start.Line && col < span.Start.Column) return false;
        if (line == span.End.Line && col > span.End.Column) return false;
        return true;
    }

    // ── Static data ──────────────────────────────────────────────────────

    private static readonly string[] s_keywords =
    [
        "fn", "let", "var", "class", "struct", "trait", "enum", "window",
        "namespace", "import", "property", "operator",
        "if", "else", "for", "in", "while", "return", "break", "continue",
        "switch", "case", "default", "when", "try", "catch", "finally",
        "throw", "using", "await", "async", "pub", "derivable",
        "is", "as", "new", "null", "true", "false", "self", "item",
        "derivateof", "windowof", "serialize", "deserialize",
        "not", "and", "or", "where", "op", "mut"
    ];

    private static readonly string[] s_builtinTypes =
    [
        "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64",
        "f32", "f64", "decimal", "bool", "char", "string", "object", "void", "handle"
    ];

    // ── Document state ───────────────────────────────────────────────────

    private sealed record DocumentState(
        string Text,
        CompilationUnitNode? Ast,
        IReadOnlyList<Diagnostic>? ParseDiagnostics);
}
