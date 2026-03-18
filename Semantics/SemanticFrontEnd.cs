using AuraLang.Ast;

namespace AuraLang.Semantics;

/// <summary>
/// 方便用：Parse 之后直接做语义检查。
/// </summary>
public static class SemanticFrontEnd
{
    public static SemanticResult Check(CompilationUnitNode cu)
        => new SemanticAnalyzer().Analyze(cu);
}
