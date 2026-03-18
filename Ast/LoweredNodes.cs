namespace AuraLang.Ast;

/// <summary>
/// Lowering-time expression node that introduces local declarations visible only within <see cref="Body"/>.
/// This is an internal IR node (not produced by the parser).
/// </summary>
public sealed record LetExprNode(SourceSpan Span, IReadOnlyList<VarDeclStmtNode> Decls, ExprNode Body) : ExprNode(Span);

/// <summary>
/// Lowering-time expression node representing a runtime type test: <c>expr is Type</c>.
/// This is an internal IR node (not produced by the parser).
/// </summary>
public sealed record TypeIsExprNode(SourceSpan Span, ExprNode Expr, TypeNode Type) : ExprNode(Span);

/// <summary>
/// Lowering-time expression node representing a runtime cast: <c>(Type)expr</c>.
/// This is an internal IR node (not produced by the parser).
/// </summary>
public sealed record CastExprNode(SourceSpan Span, ExprNode Expr, TypeNode Type) : ExprNode(Span);

/// <summary>
/// Lowering-time expression node representing a try/catch expression:
/// <code>
/// try { TryExpr } catch (Type Name) { CatchExpr }
/// </code>
/// This is an internal IR node (not produced by the parser).
/// </summary>
public sealed record TryCatchExprNode(SourceSpan Span, ExprNode TryExpr, IReadOnlyList<CatchExprClauseNode> Catches) : ExprNode(Span);

/// <summary>
/// Catch clause for <see cref="TryCatchExprNode"/>.
/// </summary>
public sealed record CatchExprClauseNode(SourceSpan Span, TypeNode Type, NameNode Name, ExprNode Body);

/// <summary>
/// Lowering-time expression node representing sequential evaluation of expressions.
/// It evaluates each expression in order and yields the value of the last one.
/// Intermediate values (if any) are discarded.
/// This is an internal IR node (not produced by the parser).
/// </summary>
public sealed record SeqExprNode(SourceSpan Span, IReadOnlyList<ExprNode> Exprs) : ExprNode(Span);
