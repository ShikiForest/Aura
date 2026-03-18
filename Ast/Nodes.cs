namespace AuraLang.Ast;

/* =========================
 *  Base
 * ========================= */

public abstract record SyntaxNode(SourceSpan Span);

public interface ICompilationItem { SourceSpan Span { get; } }
public interface ITypeMember { SourceSpan Span { get; } }

public enum Visibility { Default, Public }
public enum Mutability { Let, Var }

/* =========================
 *  Names
 * ========================= */

public sealed record NameNode(SourceSpan Span, string Text) : SyntaxNode(Span);

public sealed record QualifiedNameNode(SourceSpan Span, IReadOnlyList<NameNode> Parts) : SyntaxNode(Span)
{
    public override string ToString() => string.Join(".", Parts.Select(p => p.Text));
}

/* =========================
 *  Attributes
 * ========================= */

public sealed record AttributeSectionNode(SourceSpan Span, IReadOnlyList<AttributeNode> Attributes) : SyntaxNode(Span);

public sealed record AttributeNode(SourceSpan Span, QualifiedNameNode Name, IReadOnlyList<AttributeArgNode> Args) : SyntaxNode(Span);

public abstract record AttributeArgNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record AttributePositionalArgNode(SourceSpan Span, ExprNode Value) : AttributeArgNode(Span);

public sealed record AttributeNamedArgNode(SourceSpan Span, NameNode Name, string AssignToken, ExprNode Value) : AttributeArgNode(Span);

/* =========================
 *  Types
 * ========================= */

public abstract record TypeNode(SourceSpan Span) : SyntaxNode(Span);

public enum BuiltinTypeKind
{
    I8, I16, I32, I64,
    U8, U16, U32, U64,
    F32, F64,
    Decimal, Bool, Char, String, Object, Void,
    Handle
}

public sealed record BuiltinTypeNode(SourceSpan Span, BuiltinTypeKind Kind) : TypeNode(Span);

public sealed record NamedTypeNode(SourceSpan Span, QualifiedNameNode Name, IReadOnlyList<TypeNode> TypeArgs) : TypeNode(Span);

public sealed record NullableTypeNode(SourceSpan Span, TypeNode Inner) : TypeNode(Span);

public sealed record FunctionTypeNode(SourceSpan Span, IReadOnlyList<TypeNode> ParamTypes, TypeNode ReturnType) : TypeNode(Span);

public sealed record WindowOfTypeNode(SourceSpan Span, TypeNode Inner) : TypeNode(Span);

/* generic constraints */
public abstract record TypeConstraintNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record TypeRefConstraintNode(SourceSpan Span, TypeNode TypeRef) : TypeConstraintNode(Span);

public sealed record NewConstraintNode(SourceSpan Span) : TypeConstraintNode(Span);

public sealed record ClassConstraintNode(SourceSpan Span) : TypeConstraintNode(Span);

public sealed record StructConstraintNode(SourceSpan Span) : TypeConstraintNode(Span);

public sealed record WhereClauseNode(SourceSpan Span, NameNode TypeParamName, IReadOnlyList<TypeConstraintNode> Constraints) : SyntaxNode(Span);

public sealed record TypeParameterNode(SourceSpan Span, NameNode Name) : SyntaxNode(Span);

/* =========================
 *  Compilation Unit / Namespace / Import
 * ========================= */

public sealed record CompilationUnitNode(SourceSpan Span, IReadOnlyList<ICompilationItem> Items) : SyntaxNode(Span);

public sealed record ImportDeclNode(SourceSpan Span, QualifiedNameNode Name) : SyntaxNode(Span), ICompilationItem;

public sealed record NamespaceDeclNode(SourceSpan Span, QualifiedNameNode Name, IReadOnlyList<ICompilationItem> Members) : SyntaxNode(Span), ICompilationItem;

/* =========================
 *  Declarations
 * ========================= */

public abstract record DeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    NameNode Name
) : SyntaxNode(Span), ICompilationItem;

public sealed record TraitDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    NameNode Name,
    IReadOnlyList<FunctionSignatureNode> Members
) : DeclNode(Span, Attributes, Visibility, Name);

public abstract record TypeDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    NameNode Name,
    IReadOnlyList<TypeParameterNode> TypeParams,
    IReadOnlyList<TypeNode> BaseTypes,
    IReadOnlyList<ITypeMember> Members
) : DeclNode(Span, Attributes, Visibility, Name);

public sealed record ClassDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    NameNode Name,
    IReadOnlyList<TypeParameterNode> TypeParams,
    IReadOnlyList<TypeNode> BaseTypes,
    IReadOnlyList<ITypeMember> Members
) : TypeDeclNode(Span, Attributes, Visibility, Name, TypeParams, BaseTypes, Members);

public sealed record StructDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    NameNode Name,
    IReadOnlyList<TypeParameterNode> TypeParams,
    IReadOnlyList<TypeNode> BaseTypes,
    IReadOnlyList<ITypeMember> Members
) : TypeDeclNode(Span, Attributes, Visibility, Name, TypeParams, BaseTypes, Members);

public sealed record EnumDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    NameNode Name,
    IReadOnlyList<EnumMemberNode> Members
) : DeclNode(Span, Attributes, Visibility, Name), ITypeMember;

public sealed record EnumMemberNode(SourceSpan Span, NameNode Name, ExprNode? Value) : SyntaxNode(Span);

public sealed record WindowDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    NameNode Name,
    TypeNode TargetType,
    IReadOnlyList<WindowMemberNode> Members
) : DeclNode(Span, Attributes, Visibility, Name), ITypeMember;

public sealed record WindowMemberNode(SourceSpan Span, NameNode Name, TypeNode Type) : SyntaxNode(Span);

public sealed record FieldDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    Mutability Mutability,
    NameNode Name,
    TypeNode? Type,
    ExprNode? Init
) : SyntaxNode(Span), ITypeMember;

public enum AccessorKind { Get, Set }

public abstract record AccessorBodyNode(SourceSpan Span) : SyntaxNode(Span);
public sealed record AccessorExprBodyNode(SourceSpan Span, ExprNode Expr) : AccessorBodyNode(Span);
public sealed record AccessorBlockBodyNode(SourceSpan Span, BlockStmtNode Block) : AccessorBodyNode(Span);

public sealed record AccessorDeclNode(
    SourceSpan Span,
    AccessorKind Kind,
    AccessorBodyNode? Body
) : SyntaxNode(Span);

public sealed record PropertyDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    NameNode Name,
    TypeNode Type,
    IReadOnlyList<AccessorDeclNode> Accessors
) : SyntaxNode(Span), ITypeMember;

public enum FunctionModifier { Async, Derivable }

public abstract record ReturnSpecNode(SourceSpan Span) : SyntaxNode(Span);
public sealed record ReturnTypeSpecNode(SourceSpan Span, TypeNode ReturnType) : ReturnSpecNode(Span);
public sealed record StateSpecNode(SourceSpan Span, QualifiedNameNode StateName) : ReturnSpecNode(Span);

public sealed record ParameterNode(
    SourceSpan Span,
    NameNode Name,
    TypeNode? Type,
    ExprNode? DefaultValue
) : SyntaxNode(Span);

public abstract record FunctionBodyNode(SourceSpan Span) : SyntaxNode(Span);
public sealed record FunctionBlockBodyNode(SourceSpan Span, BlockStmtNode Block) : FunctionBodyNode(Span);
public sealed record FunctionExprBodyNode(SourceSpan Span, ExprNode Expr) : FunctionBodyNode(Span);

public sealed record FunctionSignatureNode(
    SourceSpan Span,
    Visibility Visibility,
    IReadOnlyList<FunctionModifier> Modifiers,
    NameNode Name,
    IReadOnlyList<TypeParameterNode> TypeParams,
    IReadOnlyList<ParameterNode> Parameters,
    ReturnSpecNode? ReturnSpec,
    IReadOnlyList<WhereClauseNode> WhereClauses
) : SyntaxNode(Span);

public sealed record FunctionDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    IReadOnlyList<FunctionModifier> Modifiers,
    NameNode Name,
    IReadOnlyList<TypeParameterNode> TypeParams,
    IReadOnlyList<ParameterNode> Parameters,
    ReturnSpecNode? ReturnSpec,
    IReadOnlyList<WhereClauseNode> WhereClauses,
    FunctionBodyNode Body
) : DeclNode(Span, Attributes, Visibility, Name), ITypeMember;

public sealed record OperatorDeclNode(
    SourceSpan Span,
    IReadOnlyList<AttributeSectionNode> Attributes,
    Visibility Visibility,
    string Op,
    IReadOnlyList<ParameterNode> Parameters,
    ReturnSpecNode? ReturnSpec,
    FunctionBodyNode Body
) : SyntaxNode(Span), ITypeMember;

/* =========================
 *  Statements
 * ========================= */

public abstract record StmtNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record BlockStmtNode(SourceSpan Span, IReadOnlyList<StmtNode> Statements) : StmtNode(Span);

public sealed record EmptyStmtNode(SourceSpan Span) : StmtNode(Span);

public sealed record ExprStmtNode(SourceSpan Span, ExprNode Expr) : StmtNode(Span);

public sealed record VarDeclStmtNode(
    SourceSpan Span,
    Mutability Mutability,
    NameNode Name,
    TypeNode? Type,
    ExprNode? Init
) : StmtNode(Span);

public sealed record IfStmtNode(
    SourceSpan Span,
    ExprNode Condition,
    BlockStmtNode Then,
    StmtNode? Else
) : StmtNode(Span);

public sealed record ForEachStmtNode(
    SourceSpan Span,
    NameNode ItemName,
    ExprNode Collection,
    BlockStmtNode Body
) : StmtNode(Span);

public sealed record WhileStmtNode(
    SourceSpan Span,
    ExprNode Condition,
    BlockStmtNode Body
) : StmtNode(Span);

public sealed record ReturnStmtNode(SourceSpan Span, ExprNode? Value) : StmtNode(Span);

public sealed record BreakStmtNode(SourceSpan Span) : StmtNode(Span);

public sealed record ContinueStmtNode(SourceSpan Span) : StmtNode(Span);

public sealed record ThrowStmtNode(SourceSpan Span, ExprNode? Value) : StmtNode(Span);

public sealed record OpDeclStmtNode(SourceSpan Span, NameNode Name, FunctionTypeNode Type) : StmtNode(Span);

/* try/catch/finally */
public sealed record CatchClauseNode(
    SourceSpan Span,
    NameNode? Name,
    TypeNode? Type,
    BlockStmtNode Body
) : SyntaxNode(Span);

public sealed record TryStmtNode(
    SourceSpan Span,
    BlockStmtNode TryBlock,
    IReadOnlyList<CatchClauseNode> Catches,
    BlockStmtNode? Finally
) : StmtNode(Span);

/* switch statement */
public abstract record SwitchLabelNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record CaseLabelNode(SourceSpan Span, PatternNode Pattern, ExprNode? WhenGuard) : SwitchLabelNode(Span);

public sealed record DefaultLabelNode(SourceSpan Span) : SwitchLabelNode(Span);

public sealed record SwitchSectionNode(
    SourceSpan Span,
    IReadOnlyList<SwitchLabelNode> Labels,
    IReadOnlyList<StmtNode> Statements
) : SyntaxNode(Span);

public sealed record SwitchStmtNode(
    SourceSpan Span,
    ExprNode Value,
    IReadOnlyList<SwitchSectionNode> Sections
) : StmtNode(Span);

/* using statement/declaration */
public sealed record UsingLocalDeclNode(
    SourceSpan Span,
    Mutability Mutability,
    NameNode Name,
    TypeNode? Type,
    ExprNode Init
) : SyntaxNode(Span);

public abstract record UsingResourceNode(SourceSpan Span) : SyntaxNode(Span);
public sealed record UsingDeclsResourceNode(SourceSpan Span, IReadOnlyList<UsingLocalDeclNode> Decls) : UsingResourceNode(Span);
public sealed record UsingExprResourceNode(SourceSpan Span, ExprNode Expr) : UsingResourceNode(Span);

public sealed record UsingStmtNode(
    SourceSpan Span,
    bool Await,
    UsingResourceNode Resource,
    BlockStmtNode? Body
) : StmtNode(Span);

/* =========================
 *  Expressions
 * ========================= */

public abstract record ExprNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record NameExprNode(SourceSpan Span, NameNode Name) : ExprNode(Span);

public enum LiteralKind { Null, True, False, Int, Float, String, Char }

public sealed record LiteralExprNode(SourceSpan Span, LiteralKind Kind, string RawText) : ExprNode(Span);

/* interpolated string */
public abstract record InterpPartNode(SourceSpan Span) : SyntaxNode(Span);
public sealed record InterpTextPartNode(SourceSpan Span, string Text) : InterpPartNode(Span);
public sealed record InterpExprPartNode(SourceSpan Span, ExprNode Expr) : InterpPartNode(Span);

public sealed record InterpolatedStringExprNode(SourceSpan Span, IReadOnlyList<InterpPartNode> Parts) : ExprNode(Span);

public sealed record ListLiteralExprNode(SourceSpan Span, IReadOnlyList<ExprNode> Items) : ExprNode(Span);

public sealed record NewExprNode(SourceSpan Span, TypeNode TypeRef, IReadOnlyList<ArgumentNode> Args) : ExprNode(Span);

public sealed record MemberAccessExprNode(SourceSpan Span, ExprNode Target, NameNode Member, IReadOnlyList<TypeNode> TypeArgs) : ExprNode(Span);

public sealed record IndexExprNode(SourceSpan Span, ExprNode Target, ExprNode Index) : ExprNode(Span);

public sealed record CallExprNode(SourceSpan Span, ExprNode Callee, IReadOnlyList<ArgumentNode> Args) : ExprNode(Span);

public sealed record UnaryExprNode(SourceSpan Span, string Op, ExprNode Operand) : ExprNode(Span);

public sealed record BinaryExprNode(SourceSpan Span, string Op, ExprNode Left, ExprNode Right) : ExprNode(Span);

/* special operators */
public sealed record AssignmentExprNode(SourceSpan Span, string Op, ExprNode Left, ExprNode Right) : ExprNode(Span);

public sealed record ConditionalExprNode(SourceSpan Span, ExprNode Condition, ExprNode Then, ExprNode Else) : ExprNode(Span);

public sealed record PipeExprNode(SourceSpan Span, IReadOnlyList<ExprNode> Stages) : ExprNode(Span);

public sealed record GuardExprNode(SourceSpan Span, ExprNode Expr, IReadOnlyList<ExprNode> Handlers) : ExprNode(Span);

public sealed record LambdaParamNode(SourceSpan Span, NameNode Name, TypeNode? Type) : SyntaxNode(Span);

public sealed record LambdaExprNode(SourceSpan Span, IReadOnlyList<LambdaParamNode> Parameters, ExprNode Body) : ExprNode(Span);

/* is/as */
public sealed record IsPatternExprNode(SourceSpan Span, ExprNode Expr, PatternNode Pattern) : ExprNode(Span);

public sealed record AsExprNode(SourceSpan Span, ExprNode Expr, TypeNode Type) : ExprNode(Span);

/* switch expression */
public sealed record SwitchArmNode(SourceSpan Span, PatternNode Pattern, ExprNode? WhenGuard, ExprNode Result) : SyntaxNode(Span);

public sealed record SwitchExprNode(SourceSpan Span, ExprNode Value, IReadOnlyList<SwitchArmNode> Arms) : ExprNode(Span);

/* =========================
 *  Arguments
 * ========================= */

public abstract record ArgumentNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record PositionalArgNode(SourceSpan Span, ExprNode Value) : ArgumentNode(Span);

public sealed record NamedArgNode(SourceSpan Span, NameNode Name, string AssignToken, ExprNode Value) : ArgumentNode(Span);

public sealed record PlaceholderArgNode(SourceSpan Span) : ArgumentNode(Span);

/* =========================
 *  Patterns & Const
 * ========================= */

public abstract record PatternNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record DiscardPatternNode(SourceSpan Span) : PatternNode(Span);

public sealed record VarPatternNode(SourceSpan Span, NameNode? Name) : PatternNode(Span);

public sealed record TypePatternNode(SourceSpan Span, TypeNode Type) : PatternNode(Span);

public sealed record DeclarationPatternNode(SourceSpan Span, TypeNode Type, NameNode Name) : PatternNode(Span);

public sealed record NotPatternNode(SourceSpan Span, PatternNode Inner) : PatternNode(Span);

public sealed record AndPatternNode(SourceSpan Span, PatternNode Left, PatternNode Right) : PatternNode(Span);

public sealed record OrPatternNode(SourceSpan Span, PatternNode Left, PatternNode Right) : PatternNode(Span);

public abstract record ConstNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record ConstLiteralNode(SourceSpan Span, LiteralKind Kind, string RawText) : ConstNode(Span);

public sealed record ConstNameNode(SourceSpan Span, QualifiedNameNode Name) : ConstNode(Span);

public sealed record ConstUnaryNode(SourceSpan Span, string Op, ConstNode Operand) : ConstNode(Span);

public sealed record RelationalPatternNode(SourceSpan Span, string Op, ConstNode Value) : PatternNode(Span);

public sealed record ConstantPatternNode(SourceSpan Span, ConstNode Value) : PatternNode(Span);

/* property pattern */
public sealed record PropertySubpatternNode(SourceSpan Span, NameNode Name, PatternNode Pattern) : SyntaxNode(Span);

public sealed record PropertyPatternNode(SourceSpan Span, IReadOnlyList<PropertySubpatternNode> Members) : PatternNode(Span);

/* list pattern */
public sealed record ListPatternNode(SourceSpan Span, IReadOnlyList<PatternNode> Items) : PatternNode(Span);

public sealed record ParenthesizedPatternNode(SourceSpan Span, PatternNode Inner) : PatternNode(Span);