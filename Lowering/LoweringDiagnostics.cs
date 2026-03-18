using AuraLang.Ast;

namespace AuraLang.Lowering;

public enum LoweringSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record LoweringDiagnostic(SourceSpan Span, string Code, LoweringSeverity Severity, string Message);

public sealed record LoweringResult<T>(T Ast, IReadOnlyList<LoweringDiagnostic> Diagnostics);
