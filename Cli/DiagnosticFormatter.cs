using AuraLang.Ast;
using AuraLang.Semantics;
using AuraLang.Lowering;
using AuraLang.CodeGen;

namespace AntlrCompiler.Cli;

/// <summary>Common severity understood by the CLI layer.</summary>
internal enum DiagLevel { Error, Warning, Info }

/// <summary>
/// Normalised diagnostic produced from any pipeline stage.
/// All stage-specific types are converted to this record before display.
/// </summary>
internal sealed record FormattedDiagnostic(
    DiagLevel Level,
    string?   Code,     // null for parse-stage diagnostics (no error codes)
    SourceSpan Span,
    string    Message
);

/// <summary>
/// Converts pipeline-stage diagnostics into <see cref="FormattedDiagnostic"/>
/// and formats them as strings for the terminal.
/// </summary>
internal static class DiagnosticFormatter
{
    // ── Stage normalizers ────────────────────────────────────────────────────

    /// <summary>Parse-stage diagnostics are always errors (no severity field).</summary>
    public static FormattedDiagnostic FromParse(Diagnostic d)
        => new(DiagLevel.Error, null, d.Span, d.Message);

    public static FormattedDiagnostic FromSemantic(SemanticDiagnostic d)
        => new(Map(d.Severity), d.Code, d.Span, d.Message);

    public static FormattedDiagnostic FromLowering(LoweringDiagnostic d)
        => new(Map(d.Severity), d.Code, d.Span, d.Message);

    public static FormattedDiagnostic FromCodeGen(CodeGenDiagnostic d)
        => new(Map(d.Severity), d.Code, d.Span, d.Message);

    // ── Display formatting ───────────────────────────────────────────────────

    /// <summary>
    /// Produces a line in MSBuild/VS format for clickable navigation:
    ///   {file}({line},{col+1}): {level} [{code}]: {message}
    /// Column is displayed 1-based (ANTLR stores 0-based).
    /// </summary>
    public static string Format(FormattedDiagnostic d, string sourceFile)
    {
        var line   = d.Span.Start.Line;
        var col    = d.Span.Start.Column + 1; // convert 0-based → 1-based for display
        var loc    = $"{sourceFile}({line},{col})";
        var level  = d.Level switch
        {
            DiagLevel.Error   => "error",
            DiagLevel.Warning => "warning",
            _                 => "info",
        };
        var code = d.Code is not null ? $" [{d.Code}]" : "";
        return $"{loc}: {level}{code}: {d.Message}";
    }

    // ── Shared emit helper (used by CompileCommand and CheckCommand) ──────────

    /// <summary>
    /// Emits a single diagnostic to the console:
    /// formatted line + source-context snippet (always for errors, verbose-only for warnings).
    /// </summary>
    public static void Emit(
        FormattedDiagnostic fd,
        string sourceFile,
        string[] sourceLines,
        bool verbose)
    {
        var line = Format(fd, sourceFile);

        switch (fd.Level)
        {
            case DiagLevel.Error:
                ConsoleWriter.DiagnosticError(line);
                break;
            case DiagLevel.Warning:
                ConsoleWriter.DiagnosticWarning(line);
                break;
            default:
                ConsoleWriter.DiagnosticInfo(line);
                break;
        }

        // Source-context snippet: always for errors; for warnings only when --verbose
        if (fd.Level == DiagLevel.Error || verbose)
        {
            ConsoleWriter.SourceContext(
                sourceLines,
                fd.Span.Start.Line,
                fd.Span.Start.Column); // 0-based column — ConsoleWriter handles display
        }
    }

    // ── Severity mappers ─────────────────────────────────────────────────────

    private static DiagLevel Map(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error   => DiagLevel.Error,
        DiagnosticSeverity.Warning => DiagLevel.Warning,
        _                          => DiagLevel.Info,
    };

    private static DiagLevel Map(LoweringSeverity s) => s switch
    {
        LoweringSeverity.Error   => DiagLevel.Error,
        LoweringSeverity.Warning => DiagLevel.Warning,
        _                        => DiagLevel.Info,
    };

    private static DiagLevel Map(CodeGenSeverity s) => s switch
    {
        CodeGenSeverity.Error   => DiagLevel.Error,
        CodeGenSeverity.Warning => DiagLevel.Warning,
        _                       => DiagLevel.Info,
    };
}
