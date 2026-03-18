using AuraLang.Ast;

namespace AuraLang.Semantics;

/// <summary>全局声明索引（只包含本次 CompilationUnit 内的声明）。</summary>
public sealed class SymbolIndex
{
    // full name -> type symbol
    public Dictionary<string, TypeSymbol> Types { get; } = new(StringComparer.Ordinal);

    // namespace full name -> imports list
    public Dictionary<string, List<string>> ImportsByNamespace { get; } = new(StringComparer.Ordinal);

    public bool TryGetType(string fullName, out TypeSymbol symbol) => Types.TryGetValue(fullName, out symbol!);

    public IEnumerable<TypeSymbol> AllTypes() => Types.Values;
}

/// <summary>类型符号：对应 class/struct/trait/enum/window。</summary>
public sealed class TypeSymbol
{
    public required string FullName { get; init; }
    public required TypeKind Kind { get; init; }
    public required DeclNode Decl { get; init; }
    public required string Namespace { get; init; }

    public List<TypeNode> BaseTypes { get; } = [];

    public Dictionary<string, FieldDeclNode> Fields { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PropertyDeclNode> Properties { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<FunctionDeclNode>> Functions { get; } = new(StringComparer.Ordinal);

    // Trait members（signature-only）
    public Dictionary<string, List<FunctionSignatureNode>> TraitFunctions { get; } = new(StringComparer.Ordinal);

    public EnumDeclNode? EnumDecl => Decl as EnumDeclNode;
    public WindowDeclNode? WindowDecl => Decl as WindowDeclNode;

    public override string ToString() => $"{Kind} {FullName}";
}

/// <summary>局部符号（参数/局部变量/foreach 变量等）。</summary>
public sealed record LocalSymbol(
    string Name,
    Mutability Mutability,
    TypeRef DeclaredType,
    SourceSpan Span
);
