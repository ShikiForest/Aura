using AuraLang.Ast;

namespace AuraLang.Semantics;

/// <summary>语义诊断：包含错误码与严重性。</summary>
public sealed record SemanticDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    SourceSpan Span,
    string Message)
{
    public override string ToString()
        => $"{Severity} {Code} {Span.Start.Line}:{Span.Start.Column} {Message}";
}

/// <summary>语义分析结果。</summary>
public sealed record SemanticResult(IReadOnlyList<SemanticDiagnostic> Diagnostics);
