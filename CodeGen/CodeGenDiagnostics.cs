using AuraLang.Ast;

namespace AuraLang.CodeGen;

public enum CodeGenSeverity { Error, Warning, Info }

public sealed record CodeGenDiagnostic(SourceSpan Span, string Code, CodeGenSeverity Severity, string Message);

public sealed record CodeGenResult(string OutputPath, IReadOnlyList<CodeGenDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == CodeGenSeverity.Error);
}
