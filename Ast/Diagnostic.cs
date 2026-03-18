namespace AuraLang.Ast;

public sealed record Diagnostic(SourceSpan Span, string Message);

public sealed record ParseResult<T>(T? Ast, IReadOnlyList<Diagnostic> Diagnostics);