using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AuraLang.Ast;
using AuraLang.I18n;

namespace AuraLang.Lowering;

/// <summary>
/// Lowers high-level Aura AST into a smaller set of core constructs for code generation.
/// This is a source-to-source transformation on the AST (plus a few internal IR nodes in AuraLang.Ast).
/// </summary>
public sealed class AuraLowerer
{
    private readonly List<LoweringDiagnostic> _diags = new();
    private int _tempId;

    // Contextual rewrite maps (stacked).
    // - State functions: assignment to a state-function name (e.g. `run = State.Running`) is rewritten to
    //   the hidden state field name (e.g. `__aura_state_run = State.Running`).
    private readonly Stack<Dictionary<string, NameNode>> _stateFuncToFieldStack = new();

    // Track [BuildMe]-annotated class names for builder pattern enforcement
    private readonly HashSet<string> _buildMeTypes = new(StringComparer.Ordinal);

    public LoweringResult<CompilationUnitNode> Lower(CompilationUnitNode ast)
    {
        _diags.Clear();
        _tempId = 0;
        _buildMeTypes.Clear();

        // Pre-scan: collect [BuildMe]-annotated class names
        CollectBuildMeTypes(ast);

        var lowered = LowerCompilationUnit(ast);
        return new LoweringResult<CompilationUnitNode>(lowered, _diags.ToArray());
    }

    private void CollectBuildMeTypes(CompilationUnitNode ast)
    {
        foreach (var item in ast.Items)
        {
            if (item is ClassDeclNode cd)
            {
                if (cd.Attributes.Any(sec => sec.Attributes.Any(a => a.Name.ToString() == "BuildMe")))
                    _buildMeTypes.Add(cd.Name.Text);
            }
            else if (item is NamespaceDeclNode ns)
            {
                CollectBuildMeTypesInNamespace(ns);
            }
        }
    }

    private void CollectBuildMeTypesInNamespace(NamespaceDeclNode ns)
    {
        foreach (var m in ns.Members)
        {
            if (m is ClassDeclNode cd)
            {
                if (cd.Attributes.Any(sec => sec.Attributes.Any(a => a.Name.ToString() == "BuildMe")))
                    _buildMeTypes.Add(cd.Name.Text);
            }
        }
    }

    private CompilationUnitNode LowerCompilationUnit(CompilationUnitNode cu)
    {
        var items = new List<ICompilationItem>(cu.Items.Count);
        foreach (var item in cu.Items)
            items.Add(LowerCompilationItem(item));
        return new CompilationUnitNode(cu.Span, items);
    }

    private ICompilationItem LowerCompilationItem(ICompilationItem item) =>
        item switch
        {
            NamespaceDeclNode ns => LowerNamespace(ns),
            FunctionDeclNode fn => LowerFunction(fn),
            ClassDeclNode c => LowerClass(c),
            StructDeclNode s => LowerStruct(s),
            _ => item,
        };

    private NamespaceDeclNode LowerNamespace(NamespaceDeclNode ns)
    {
        var members = new List<ICompilationItem>(ns.Members.Count);
        foreach (var m in ns.Members)
            members.Add(LowerCompilationItem(m));
        return new NamespaceDeclNode(ns.Span, ns.Name, members);
    }

    private ClassDeclNode LowerClass(ClassDeclNode c)
    {
        var members = LowerTypeMembers(c.Members);
        return new ClassDeclNode(c.Span, c.Attributes, c.Visibility, c.Name, c.TypeParams, c.BaseTypes, members);
    }

    private StructDeclNode LowerStruct(StructDeclNode s)
    {
        var members = LowerTypeMembers(s.Members);
        return new StructDeclNode(s.Span, s.Attributes, s.Visibility, s.Name, s.TypeParams, s.BaseTypes, members);
    }

    /// <summary>
    /// Shared lowering logic for class/struct members: state function expansion,
    /// derivable op synthesis, and recursive member lowering.
    /// </summary>
    private List<ITypeMember> LowerTypeMembers(IReadOnlyList<ITypeMember> sourceMembers)
    {
        // Pre-scan state function groups.
        var stateGroups = sourceMembers
            .OfType<FunctionDeclNode>()
            .Where(fn => fn.ReturnSpec is StateSpecNode)
            .GroupBy(fn => fn.Name.Text, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Synthesize state fields for each group.
        var synthesized = new List<ITypeMember>();
        var stateMap = new Dictionary<string, NameNode>(StringComparer.Ordinal);

        foreach (var (fname, fns) in stateGroups)
        {
            var first = fns.Count > 0 ? (StateSpecNode?)fns[0].ReturnSpec : null;
            if (first is null || first.StateName.Parts.Count < 2)
            {
                if (fns.Count > 0)
                {
                    _diags.Add(new LoweringDiagnostic(fns[0].Span, "LWR4001", LoweringSeverity.Error,
                        Msg.Diag("LWR4001", fname)));
                }
                continue;
            }

            var fieldName = new NameNode(fns[0].Span, $"__aura_state_{fname}");
            stateMap[fname] = fieldName;

            var enumTypeName = TakeAllButLast(first.StateName);
            var enumType = new NamedTypeNode(fns[0].Span, enumTypeName, Array.Empty<TypeNode>());

            synthesized.Add(new FieldDeclNode(
                fns[0].Span,
                Array.Empty<AttributeSectionNode>(),
                Visibility.Default,
                Mutability.Var,
                fieldName,
                enumType,
                Init: null
            ));
        }

        _stateFuncToFieldStack.Push(stateMap);
        try
        {
            var members = new List<ITypeMember>(sourceMembers.Count + synthesized.Count);
            members.AddRange(synthesized);

            // Lower existing members, skipping state-impl functions (handled later).
            foreach (var m in sourceMembers)
            {
                if (m is FunctionDeclNode fn)
                {
                    if (fn.ReturnSpec is StateSpecNode)
                        continue;

                    if (fn.Modifiers.Contains(FunctionModifier.Derivable))
                    {
                        var (loweredFn, extraMembers) = LowerDerivableFunction(fn);
                        members.AddRange(extraMembers);
                        members.Add(loweredFn);
                        continue;
                    }
                }

                members.Add(LowerTypeMember(m));
            }

            // Expand state groups: impl methods + dispatcher.
            foreach (var (fname, fns) in stateGroups)
            {
                if (!stateMap.TryGetValue(fname, out var stateField))
                    continue;

                var implNameByStateValue = new Dictionary<string, NameNode>(StringComparer.Ordinal);
                foreach (var sfn in fns)
                {
                    if (sfn.ReturnSpec is not StateSpecNode ss || ss.StateName.Parts.Count < 2)
                        continue;

                    var stateValue = ss.StateName.Parts[^1].Text;
                    var implName = new NameNode(sfn.Span, $"__aura_state_{fname}__{stateValue}");
                    implNameByStateValue[stateValue] = implName;

                    var implFn = new FunctionDeclNode(
                        sfn.Span,
                        sfn.Attributes,
                        sfn.Visibility,
                        sfn.Modifiers,
                        implName,
                        sfn.TypeParams,
                        sfn.Parameters,
                        null,
                        sfn.WhereClauses,
                        sfn.Body
                    );
                    members.Add(LowerFunction(implFn));
                }

                if (fns.Count > 0)
                {
                    var dispatcher = BuildStateDispatcher(fns[0], fname, stateField, implNameByStateValue);
                    members.Add(LowerFunction(dispatcher));
                }
            }

            return members;
        }
        finally
        {
            _stateFuncToFieldStack.Pop();
        }
    }

    // -----------------------------
    // v4: State Functions helpers
    // -----------------------------

    private bool TryGetStateFieldForFunction(string funcName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NameNode? fieldName)
    {
        fieldName = null;
        if (_stateFuncToFieldStack.Count == 0)
            return false;
        return _stateFuncToFieldStack.Peek().TryGetValue(funcName, out fieldName!);
    }

    private static QualifiedNameNode TakeAllButLast(QualifiedNameNode qn)
    {
        if (qn.Parts.Count <= 1)
            return qn;
        var parts = qn.Parts.Take(qn.Parts.Count - 1).ToList();
        return new QualifiedNameNode(qn.Span, parts);
    }

    private FunctionDeclNode BuildStateDispatcher(
        FunctionDeclNode sigSource,
        string funcName,
        NameNode stateField,
        Dictionary<string, NameNode> implNameByStateValue)
    {
        var ss = sigSource.ReturnSpec as StateSpecNode;
        var enumType = ss is not null ? TakeAllButLast(ss.StateName) : new QualifiedNameNode(sigSource.Span, new List<NameNode> { new NameNode(sigSource.Span, "State") });

        // Forward arguments.
        var fwdArgs = sigSource.Parameters
            .Select(p => (ArgumentNode)new PositionalArgNode(p.Span, new NameExprNode(p.Span, p.Name)))
            .ToList();

        // Default else: return.
        StmtNode elseNode = new BlockStmtNode(sigSource.Span, new List<StmtNode> { new ReturnStmtNode(sigSource.Span, null) });

        // Build nested if chain in reverse.
        foreach (var kv in implNameByStateValue.OrderBy(k => k.Key, StringComparer.Ordinal).Reverse())
        {
            var stateValueName = new NameNode(sigSource.Span, kv.Key);
            var stateQnParts = enumType.Parts.Concat(new[] { stateValueName }).ToList();
            var stateConstExpr = QualifiedNameToExpr(new QualifiedNameNode(sigSource.Span, stateQnParts));

            var cond = new BinaryExprNode(sigSource.Span,
                "==",
                new NameExprNode(sigSource.Span, stateField),
                stateConstExpr);

            var implCall = new CallExprNode(sigSource.Span,
                new NameExprNode(sigSource.Span, kv.Value),
                fwdArgs);

            var thenBlock = new BlockStmtNode(sigSource.Span, new List<StmtNode>
            {
                new ExprStmtNode(sigSource.Span, implCall),
                new ReturnStmtNode(sigSource.Span, null)
            });

            elseNode = new IfStmtNode(sigSource.Span, cond, thenBlock, elseNode);
        }

        var body = new FunctionBlockBodyNode(sigSource.Span,
            new BlockStmtNode(sigSource.Span, new List<StmtNode> { elseNode }));

        return new FunctionDeclNode(
            sigSource.Span,
            sigSource.Attributes,
            sigSource.Visibility,
            sigSource.Modifiers,
            new NameNode(sigSource.Span, funcName),
            sigSource.TypeParams,
            sigSource.Parameters,
            null,
            sigSource.WhereClauses,
            body
        );
    }

    // -----------------------------
    // v4: Derivable / op lowering
    // -----------------------------

    private FunctionTypeNode? TryGetOpDeclType(OpDeclStmtNode op)
    {
        // Keep lowering resilient across AST shape changes.
        // Known/likely property names: OpType, Type, FuncType, FType
        var t = op.GetType().GetProperty("OpType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(op) as FunctionTypeNode
            ?? op.GetType().GetProperty("Type", BindingFlags.Public | BindingFlags.Instance)?.GetValue(op) as FunctionTypeNode
            ?? op.GetType().GetProperty("FuncType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(op) as FunctionTypeNode
            ?? op.GetType().GetProperty("FType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(op) as FunctionTypeNode;
        return t;
    }

    private (FunctionDeclNode loweredFn, List<ITypeMember> extraMembers) LowerDerivableFunction(FunctionDeclNode fn)
    {
        if (fn.Body is not FunctionBlockBodyNode fb)
            return (LowerFunction(fn), new List<ITypeMember>());

        var extraMembers = new List<ITypeMember>();
        var newStatements = new List<StmtNode>(fb.Block.Statements.Count);

        
        var opNames = new HashSet<string>(StringComparer.Ordinal);
foreach (var st in fb.Block.Statements)
        {
            if (st is OpDeclStmtNode op)
            {
                opNames.Add(op.Name.Text);

                var opType = TryGetOpDeclType(op);
                if (opType is null)
                {
                    _diags.Add(new LoweringDiagnostic(op.Span, "LWR4002", LoweringSeverity.Error,
                        Msg.Diag("LWR4002", op.Name.Text, fn.Name.Text)));
                    continue;
                }

                // Create an injectable property on the containing type.
                var propName = new NameNode(op.Span, $"__aura_op_{fn.Name.Text}_{op.Name.Text}");

                extraMembers.Add(new PropertyDeclNode(
                    op.Span,
                    Array.Empty<AttributeSectionNode>(),
                    Visibility.Public,
                    propName,
                    opType,
                    new AccessorDeclNode[]
                    {
                        new AccessorDeclNode(op.Span, AccessorKind.Get, null),
                        new AccessorDeclNode(op.Span, AccessorKind.Set, null)
                    }
                ));

                // Replace the op decl with a local alias.
                newStatements.Add(new VarDeclStmtNode(
                    op.Span,
                    Mutability.Let,
                    op.Name,
                    opType,
                    new NameExprNode(op.Span, propName)
                ));
                continue;
            }

            newStatements.Add(st);
        }

        
        // v5: Null-safe op invocation. If an op delegate is null, skip invoking it.
        var rewrittenStatements = newStatements.Select(st => RewriteDerivableOpInvokes(st, opNames)).ToList();

var rewrittenBody = new FunctionBlockBodyNode(fb.Span, new BlockStmtNode(fb.Block.Span, rewrittenStatements));
        var rewrittenFn = new FunctionDeclNode(
            fn.Span,
            fn.Attributes,
            fn.Visibility,
            fn.Modifiers,
            fn.Name,
            fn.TypeParams,
            fn.Parameters,
            fn.ReturnSpec,
            fn.WhereClauses,
            rewrittenBody
        );

        var loweredFn = LowerFunction(rewrittenFn);
        var loweredExtras = extraMembers.Select(LowerTypeMember).ToList();
        return (loweredFn, loweredExtras);
    }

    
private StmtNode RewriteDerivableOpInvokes(StmtNode st, HashSet<string> opNames)
{
    switch (st)
    {
        case BlockStmtNode b:
            return new BlockStmtNode(b.Span, b.Statements.Select(s => RewriteDerivableOpInvokes(s, opNames)).ToList());

        case IfStmtNode iff:
            return new IfStmtNode(
                iff.Span,
                iff.Condition,
                (BlockStmtNode)RewriteDerivableOpInvokes(iff.Then, opNames),
                iff.Else is null ? null : RewriteDerivableOpInvokes(iff.Else, opNames)
            );

        case ExprStmtNode es:
            return RewriteDerivableOpInvokeExprStmt(es, opNames);

        default:
            return st;
    }
}

private StmtNode RewriteDerivableOpInvokeExprStmt(ExprStmtNode es, HashSet<string> opNames)
{
    // Rewrite:
    //   before(...);
    // into:
    //   if (before != null) { before(...); }
    //
    // (Derivable ops are optional; when they aren't provided, they are null.)
    if (es.Expr is CallExprNode call && call.Callee is NameExprNode ne && opNames.Contains(ne.Name.Text))
    {
        var nameExpr = new NameExprNode(es.Span, ne.Name);
        var cond = new BinaryExprNode(es.Span, "!=", nameExpr, new LiteralExprNode(es.Span, LiteralKind.Null, "null"));
        var thenBlock = new BlockStmtNode(es.Span, new List<StmtNode> { es });
        return new IfStmtNode(es.Span, cond, thenBlock, @Else: null);
    }

    return es;
}

private ITypeMember LowerTypeMember(ITypeMember m) =>
        m switch
        {
            FunctionDeclNode fn => LowerFunction(fn),
            PropertyDeclNode p => LowerProperty(p),
            FieldDeclNode f => LowerField(f),
            OperatorDeclNode op => LowerOperatorDecl(op),
            _ => m,
        };

    private FieldDeclNode LowerField(FieldDeclNode f)
    {
        var init = f.Init is null ? null : LowerExpr(f.Init);
        return new FieldDeclNode(f.Span, f.Attributes, f.Visibility, f.Mutability, f.Name, f.Type, init);
    }

    private PropertyDeclNode LowerProperty(PropertyDeclNode p)
    {
        var accessors = p.Accessors.Select(LowerAccessor).ToList();
        return new PropertyDeclNode(p.Span, p.Attributes, p.Visibility, p.Name, p.Type, accessors);
    }

    private AccessorDeclNode LowerAccessor(AccessorDeclNode a)
    {
        var body = a.Body switch
        {
            AccessorBlockBodyNode b => (AccessorBodyNode)new AccessorBlockBodyNode(b.Span, LowerBlock(b.Block)),
            AccessorExprBodyNode e => new AccessorExprBodyNode(e.Span, LowerExpr(e.Expr)),
            _ => a.Body
        };
        return a with { Body = body };
    }

    private OperatorDeclNode LowerOperatorDecl(OperatorDeclNode op)
    {
        var body = op.Body switch
        {
            FunctionBlockBodyNode b => (FunctionBodyNode)new FunctionBlockBodyNode(b.Span, LowerBlock(b.Block)),
            FunctionExprBodyNode e => new FunctionExprBodyNode(e.Span, LowerExpr(e.Expr)),
            _ => op.Body
        };
        return new OperatorDeclNode(op.Span, op.Attributes, op.Visibility, op.Op, op.Parameters, op.ReturnSpec, body);
    }

    private FunctionDeclNode LowerFunction(FunctionDeclNode fn)
{
    var body = fn.Body switch
    {
        FunctionBlockBodyNode b => new FunctionBlockBodyNode(b.Span, LowerBlock(b.Block)),
        FunctionExprBodyNode e => new FunctionExprBodyNode(e.Span, LowerExpr(e.Expr)),
        _ => fn.Body
    };

    var lowered = new FunctionDeclNode(
        fn.Span,
        fn.Attributes,
        fn.Visibility,
        fn.Modifiers,
        fn.Name,
        fn.TypeParams,
        fn.Parameters,
        fn.ReturnSpec,
        fn.WhereClauses,
        body
    );

    // Async lowering: rewrite await in async functions into non-blocking Task continuations.
    // (We keep the Async modifier so codegen can wrap the return type to Task/Task<T>.)
    if (lowered.Modifiers.Contains(FunctionModifier.Async))
        lowered = LowerAsyncFunction(lowered);

    return lowered;
}


// -----------------------------
// Async lowering (non-blocking)
// -----------------------------

private FunctionDeclNode LowerAsyncFunction(FunctionDeclNode fn)
{
    // Only functions with bodies can be lowered. If we can't recognize the body form,
    // just leave it as-is.
    var userReturnType = (fn.ReturnSpec as ReturnTypeSpecNode)?.ReturnType; // null => void

    BlockStmtNode? block = fn.Body switch
    {
        FunctionBlockBodyNode b => b.Block,
        FunctionExprBodyNode e => new BlockStmtNode(e.Span, new List<StmtNode>
        {
            new ReturnStmtNode(e.Span, e.Expr)
        }),
        _ => null
    };

    if (block is null)
        return fn;

    // Validate whether we can perform *non-blocking* lowering.
    // We only support await in "statement boundary" positions:
    //   - let/var x = await expr;
    //   - await expr;
    //   - x = await expr;
    //   - return await expr;
    //
    // Anything else falls back to "blocking await" semantics (still returns Task/Task<T>).
    var canLowerNonBlocking = CanLowerAsyncNonBlocking(block);

    if (!canLowerNonBlocking)
    {
        // Blocking fallback: wrap the entire body in Task.Run(() => { body })
        // so the method doesn't block the calling thread synchronously.
        // The inner body still uses GetAwaiter().GetResult() for await expressions,
        // but runs on a thread pool thread.

        // Build Task.Run(() => { body }) wrapping the blocking fallback body
        var emptyParams = Array.Empty<LambdaParamNode>().ToList();

        if (userReturnType is not null)
        {
            // Task<T>: Task.Run<T>(() => blockAsExpr)
            var taskRunCallee = new MemberAccessExprNode(block.Span,
                new NameExprNode(block.Span, new NameNode(block.Span, "System.Threading.Tasks.Task")),
                new NameNode(block.Span, "Run"),
                new List<TypeNode> { userReturnType });

            var taskRunExpr = new CallExprNode(block.Span, taskRunCallee,
                new List<ArgumentNode>
                {
                    new PositionalArgNode(block.Span,
                        new LambdaExprNode(block.Span, emptyParams,
                            MakeBlockAsExpr(block, userReturnType)))
                });
            var taskRunBody = new FunctionBlockBodyNode(block.Span,
                new BlockStmtNode(block.Span, new List<StmtNode>
                {
                    new ReturnStmtNode(block.Span, taskRunExpr)
                }));
            return fn with { Body = taskRunBody };
        }
        else
        {
            // Task (void): Task.Run(() => blockAsExpr)
            var taskRunCallee = new MemberAccessExprNode(block.Span,
                new NameExprNode(block.Span, new NameNode(block.Span, "System.Threading.Tasks.Task")),
                new NameNode(block.Span, "Run"),
                Array.Empty<TypeNode>());

            var taskRunExpr = new CallExprNode(block.Span, taskRunCallee,
                new List<ArgumentNode>
                {
                    new PositionalArgNode(block.Span,
                        new LambdaExprNode(block.Span, emptyParams,
                            MakeBlockAsExpr(block, null)))
                });
            var taskRunBody = new FunctionBlockBodyNode(block.Span,
                new BlockStmtNode(block.Span, new List<StmtNode>
                {
                    new ReturnStmtNode(block.Span, taskRunExpr)
                }));
            return fn with { Body = taskRunBody };
        }
    }

    // Non-blocking lowering:
    //  - Keep the prefix (before first await) as normal statements, but rewrite returns to Task.
    //  - Rewrite the suffix (from first await) into a single `return <task-chain-expr>;`
    var stmts = block.Statements;
    var firstAwaitIndex = -1;
    for (var i = 0; i < stmts.Count; i++)
    {
        if (ContainsAwaitInStmt(stmts[i]))
        {
            firstAwaitIndex = i;
            break;
        }
    }

    // No await at all: just rewrite returns and add CompletedTask if needed.
    if (firstAwaitIndex < 0)
    {
        var rewritten = RewriteReturnsForAsync(block, userReturnType);

        if (userReturnType is null && !EndsWithReturn(rewritten))
        {
            rewritten = new BlockStmtNode(
                rewritten.Span,
                rewritten.Statements.Concat(new[]
                {
                    new ReturnStmtNode(rewritten.Span, MakeCompletedTaskExpr(rewritten.Span))
                }).ToList()
            );
        }

        return fn with { Body = new FunctionBlockBodyNode(block.Span, rewritten) };
    }

    var prefix = stmts.Take(firstAwaitIndex).ToList();
    var suffix = stmts.Skip(firstAwaitIndex).ToList();

    // Rewrite returns in the prefix to return Task/Task<T>.
    var prefixRewritten = prefix.Select(st => RewriteReturnsForAsync(st, userReturnType)).ToList();

    // Build the non-blocking continuation chain for the suffix.
    var taskExpr = BuildAsyncTaskExpr(suffix, userReturnType);

    var newStatements = new List<StmtNode>();
    newStatements.AddRange(prefixRewritten);
    newStatements.Add(new ReturnStmtNode(block.Span, taskExpr));

    var newBlock = new BlockStmtNode(block.Span, newStatements);
    return fn with { Body = new FunctionBlockBodyNode(block.Span, newBlock) };
}

private bool CanLowerAsyncNonBlocking(BlockStmtNode block)
{
    // Conservative validation: if we see an await in any unsupported position,
    // we fall back to blocking semantics and emit a diagnostic.
    var ok = true;

    void VisitStmt(StmtNode st)
    {
        switch (st)
        {
            case BlockStmtNode b:
                foreach (var s in b.Statements) VisitStmt(s);
                return;

            case IfStmtNode iff:
                // Condition must not contain await.
                if (ContainsAwait(iff.Condition))
                {
                    _diags.Add(new LoweringDiagnostic(
                        iff.Span,
                        "AURLW3001",
                        LoweringSeverity.Warning,
                        Msg.Diag("AURLW3001")
                    ));
                    ok = false;
                }
                VisitStmt(iff.Then);
                if (iff.Else is not null) VisitStmt(iff.Else);
                return;

            case ReturnStmtNode r:
                if (r.Value is not null && ContainsAwait(r.Value) && r.Value is not UnaryExprNode { Op: "await" })
                {
                    _diags.Add(new LoweringDiagnostic(
                        r.Span,
                        "AURLW3002",
                        LoweringSeverity.Warning,
                        Msg.Diag("AURLW3002")
                    ));
                    ok = false;
                }
                return;

            case VarDeclStmtNode v:
                if (v.Init is not null && ContainsAwait(v.Init) && v.Init is not UnaryExprNode { Op: "await" })
                {
                    _diags.Add(new LoweringDiagnostic(
                        v.Span,
                        "AURLW3003",
                        LoweringSeverity.Warning,
                        Msg.Diag("AURLW3003")
                    ));
                    ok = false;
                }
                return;

            case ExprStmtNode es:
                if (ContainsAwait(es.Expr))
                {
                    // Supported patterns:
                    //   await expr;
                    //   x = await expr;
                    if (es.Expr is UnaryExprNode { Op: "await" })
                        return;

                    if (es.Expr is AssignmentExprNode { Op: "=", Right: UnaryExprNode { Op: "await" } })
                        return;

                    _diags.Add(new LoweringDiagnostic(
                        es.Span,
                        "AURLW3004",
                        LoweringSeverity.Warning,
                        Msg.Diag("AURLW3004")
                    ));
                    ok = false;
                }
                return;

            // For now, we treat any other statement kind containing await as unsupported.
            default:
                if (ContainsAwaitInStmt(st))
                {
                    _diags.Add(new LoweringDiagnostic(
                        st.Span,
                        "AURLW3005",
                        LoweringSeverity.Warning,
                        Msg.Diag("AURLW3005", st.GetType().Name)
                    ));
                    ok = false;
                }
                return;
        }
    }

    foreach (var st in block.Statements)
        VisitStmt(st);

    return ok;
}

private static bool EndsWithReturn(BlockStmtNode block)
    => block.Statements.Count > 0 && block.Statements[^1] is ReturnStmtNode;

private BlockStmtNode RewriteReturnsForAsync(BlockStmtNode block, TypeNode? userReturnType)
{
    var rewritten = block.Statements.Select(st => RewriteReturnsForAsync(st, userReturnType)).ToList();
    return new BlockStmtNode(block.Span, rewritten);
}

private StmtNode RewriteReturnsForAsync(StmtNode st, TypeNode? userReturnType)
{
    switch (st)
    {
        case BlockStmtNode b:
            return new BlockStmtNode(b.Span, b.Statements.Select(s => RewriteReturnsForAsync(s, userReturnType)).ToList());

        case IfStmtNode iff:
            return new IfStmtNode(
                iff.Span,
                iff.Condition,
                RewriteReturnsForAsync(iff.Then, userReturnType),
                iff.Else is null ? null : RewriteReturnsForAsync(iff.Else, userReturnType)
            );

        case ReturnStmtNode r:
        {
            // Note: If r.Value contains await, it will be handled in the non-blocking lowering
            // (i.e., in BuildAsyncTaskExpr). Here we only handle synchronous returns.
            if (userReturnType is null)
            {
                // async-void (Task): return CompletedTask.
                if (r.Value is null)
                    return new ReturnStmtNode(r.Span, MakeCompletedTaskExpr(r.Span));

                // Preserve side effects (if any), but ultimately return CompletedTask.
                return new ReturnStmtNode(r.Span, new SeqExprNode(r.Span, new ExprNode[]
                {
                    r.Value,
                    MakeCompletedTaskExpr(r.Span)
                }));
            }
            else
            {
                if (r.Value is null)
                {
                    // Can't synthesize a default(T) without type analysis; return a faulted task.
                    return new ReturnStmtNode(r.Span, MakeTaskFromExceptionExpr(r.Span, userReturnType,
                        "async function returned without a value"));
                }

                return new ReturnStmtNode(r.Span, MakeTaskFromResultExpr(r.Span, userReturnType, r.Value));
            }
        }

        default:
            return st;
    }
}

private ExprNode BuildAsyncTaskExpr(IReadOnlyList<StmtNode> statements, TypeNode? userReturnType)
{
    // Monadic-ish lowering into Task chains:
    //   - sequences of non-await statements become Let/Seq wrappers
    //   - each await becomes: tempTask = <expr>; return Unwrap(tempTask.ContinueWith(...))

    if (statements.Count == 0)
    {
        if (userReturnType is null)
            return MakeCompletedTaskExpr(default(SourceSpan));

        return MakeTaskFromExceptionExpr(default(SourceSpan), userReturnType, "async function reached end without returning a value");
    }

    // Flatten a leading single block, to reduce nesting.
    if (statements.Count == 1 && statements[0] is BlockStmtNode bb)
        return BuildAsyncTaskExpr(bb.Statements, userReturnType);

    var span = statements[0].Span;

    var decls = new List<VarDeclStmtNode>();
    var sideEffects = new List<ExprNode>();

    var i = 0;
    for (; i < statements.Count; i++)
    {
        var st = statements[i];

        // Stop before control flow or await boundaries.
        if (st is IfStmtNode or ReturnStmtNode)
            break;

        if (ContainsAwaitInStmt(st))
            break;

        switch (st)
        {
            case VarDeclStmtNode v:
                decls.Add(v);
                continue;

            case ExprStmtNode es:
                sideEffects.Add(es.Expr);
                continue;

            case BlockStmtNode b:
                // Inline blocks: treat them as side-effect sequences.
                // (We only do this if the block itself has no await, which is ensured above.)
                foreach (var inner in b.Statements)
                {
                    if (inner is VarDeclStmtNode iv)
                        decls.Add(iv);
                    else if (inner is ExprStmtNode ies)
                        sideEffects.Add(ies.Expr);
                    else
                        // For anything else, stop and handle via recursion.
                        goto stop_prefix;
                }
                continue;

            default:
                // Unknown statement kind in prefix chunk: stop and handle via recursion.
                break;
        }

        break;
    }

stop_prefix:

    IReadOnlyList<StmtNode> rest = statements.Skip(i).ToList();

    ExprNode tail;
    if (rest.Count == 0)
    {
        tail = userReturnType is null
            ? MakeCompletedTaskExpr(span)
            : MakeTaskFromExceptionExpr(span, userReturnType, "async function reached end without returning a value");
    }
    else
    {
        var head = rest[0];
        var after = rest.Skip(1).ToList();

        tail = head switch
        {
            // return ...
            ReturnStmtNode r => MakeReturnTaskExpr(r, userReturnType),

            // if ...
            IfStmtNode iff => MakeIfTaskExpr(iff, after, userReturnType),

            // let/var x = await ...
            VarDeclStmtNode v when v.Init is UnaryExprNode { Op: "await" } =>
                MakeAwaitVarDeclChain(v, after, userReturnType),

            // await ...
            ExprStmtNode es when es.Expr is UnaryExprNode { Op: "await" } =>
                MakeAwaitExprChain((UnaryExprNode)es.Expr, after, userReturnType),

            // x = await ...
            ExprStmtNode es when es.Expr is AssignmentExprNode { Op: "=", Right: UnaryExprNode { Op: "await" } } =>
                MakeAwaitAssignmentChain((AssignmentExprNode)es.Expr, after, userReturnType),

            // block ...
            BlockStmtNode b => BuildAsyncTaskExpr(b.Statements.Concat(after).ToList(), userReturnType),

            // fallback
            _ => MakeTaskFromExceptionExpr(head.Span, userReturnType, $"unsupported statement in async lowering: {head.GetType().Name}")
        };
    }

    // Wrap prefix decls + side effects.
    ExprNode wrapped = tail;
    if (sideEffects.Count > 0)
    {
        var exprs = sideEffects.Concat(new[] { wrapped }).ToList();
        wrapped = new SeqExprNode(span, exprs);
    }

    if (decls.Count > 0)
        wrapped = new LetExprNode(span, decls, wrapped);

    return wrapped;
}

private ExprNode MakeIfTaskExpr(IfStmtNode iff, IReadOnlyList<StmtNode> after, TypeNode? userReturnType)
{
    // Desugar:
    //   if (cond) { thenStmts } else { elseStmts }
    //   <after>
    // into:
    //   cond ? TaskExpr(thenStmts + after) : TaskExpr(elseStmts + after)

    if (ContainsAwait(iff.Condition))
    {
        // Should have been rejected in CanLowerAsyncNonBlocking; keep it safe anyway.
        _diags.Add(new LoweringDiagnostic(
            iff.Span,
            "AURLW3006",
            LoweringSeverity.Warning,
            Msg.Diag("AURLW3006")
        ));
    }

    var thenList = StmtToList(iff.Then).Concat(after).ToList();
    var elseList = (iff.Else is null ? new List<StmtNode>() : StmtToList(iff.Else)).Concat(after).ToList();

    var thenExpr = BuildAsyncTaskExpr(thenList, userReturnType);
    var elseExpr = BuildAsyncTaskExpr(elseList, userReturnType);

    return new ConditionalExprNode(iff.Span, iff.Condition, thenExpr, elseExpr);
}

private ExprNode MakeReturnTaskExpr(ReturnStmtNode r, TypeNode? userReturnType)
{
    if (userReturnType is null)
    {
        // Task-returning async function
        if (r.Value is null)
            return MakeCompletedTaskExpr(r.Span);

        if (r.Value is UnaryExprNode { Op: "await" } u)
        {
            // return await expr;  (void)
            return MakeAwaitExprChain(u, new List<StmtNode>
            {
                // after await, just return CompletedTask
                new ReturnStmtNode(r.Span, null)
            }, userReturnType);
        }

        // Preserve side effects but return CompletedTask.
        return new SeqExprNode(r.Span, new ExprNode[]
        {
            r.Value,
            MakeCompletedTaskExpr(r.Span)
        });
    }
    else
    {
        if (r.Value is null)
            return MakeTaskFromExceptionExpr(r.Span, userReturnType, "async function returned without a value");

        if (r.Value is UnaryExprNode { Op: "await" } u)
            return MakeReturnAwaitTaskExpr(u, userReturnType);

        return MakeTaskFromResultExpr(r.Span, userReturnType, r.Value);
    }
}

private ExprNode MakeAwaitVarDeclChain(VarDeclStmtNode v, IReadOnlyList<StmtNode> after, TypeNode? userReturnType)
{
    // let x = await expr; <after>
    // =>
    // let __aura_taskN = expr;
    // return Unwrap(__aura_taskN.ContinueWith<Task<Ret>>(_ => { let x = await __aura_taskN; <after-task> }))
    var awaitExpr = (UnaryExprNode)v.Init!;
    var operand = awaitExpr.Operand;

    var tempTaskName = FreshTempName(v.Span, "__aura_task");
    var tempTaskDecl = new VarDeclStmtNode(v.Span, Mutability.Let, tempTaskName, null, operand);

    var contParamName = FreshTempName(v.Span, "__aura_await");
    var contParam = new LambdaParamNode(v.Span, contParamName, MakeClrTypeNode(v.Span, "System.Threading.Tasks.Task"));

    var awaitedValue = new UnaryExprNode(v.Span, "await", new NameExprNode(v.Span, tempTaskName));

    var valueDecl = new VarDeclStmtNode(v.Span, v.Mutability, v.Name, v.Type, awaitedValue);

    var restTaskExpr = BuildAsyncTaskExpr(after, userReturnType);

    // In the continuation: bind x, then continue.
    var lambdaBody = (ExprNode)new LetExprNode(v.Span, new List<VarDeclStmtNode> { valueDecl }, restTaskExpr);
    var lambda = new LambdaExprNode(v.Span, new List<LambdaParamNode> { contParam }, lambdaBody);

    var contResultType = MakeTaskTypeNode(v.Span, userReturnType);

    var continueWithCall = new CallExprNode(
        v.Span,
        new MemberAccessExprNode(v.Span, new NameExprNode(v.Span, tempTaskName), new NameNode(v.Span, "ContinueWith"), new List<TypeNode> { contResultType }),
        new List<ArgumentNode> { new PositionalArgNode(v.Span, lambda) }
    );

    var unwrapped = MakeUnwrapCall(v.Span, continueWithCall, userReturnType);

    return new LetExprNode(v.Span, new List<VarDeclStmtNode> { tempTaskDecl }, unwrapped);
}

private ExprNode MakeAwaitAssignmentChain(AssignmentExprNode assign, IReadOnlyList<StmtNode> after, TypeNode? userReturnType)
{
    // x = await expr; <after>
    // =>
    // let __aura_taskN = expr;
    // return Unwrap(__aura_taskN.ContinueWith<Task<Ret>>(_ => { x = await __aura_taskN; <after-task> }))

    var awaitExpr = (UnaryExprNode)assign.Right;
    var operand = awaitExpr.Operand;

    var tempTaskName = FreshTempName(assign.Span, "__aura_task");
    var tempTaskDecl = new VarDeclStmtNode(assign.Span, Mutability.Let, tempTaskName, null, operand);

    var contParamName = FreshTempName(assign.Span, "__aura_await");
    var contParam = new LambdaParamNode(assign.Span, contParamName, MakeClrTypeNode(assign.Span, "System.Threading.Tasks.Task"));

    var awaitedValue = new UnaryExprNode(assign.Span, "await", new NameExprNode(assign.Span, tempTaskName));

    var assignAfterAwait = new AssignmentExprNode(assign.Span, assign.Op, assign.Left, awaitedValue);

    var restTaskExpr = BuildAsyncTaskExpr(after, userReturnType);

    var lambdaBody = (ExprNode)new SeqExprNode(assign.Span, new ExprNode[] { assignAfterAwait, restTaskExpr });
    var lambda = new LambdaExprNode(assign.Span, new List<LambdaParamNode> { contParam }, lambdaBody);

    var contResultType = MakeTaskTypeNode(assign.Span, userReturnType);

    var continueWithCall = new CallExprNode(
        assign.Span,
        new MemberAccessExprNode(assign.Span, new NameExprNode(assign.Span, tempTaskName), new NameNode(assign.Span, "ContinueWith"), new List<TypeNode> { contResultType }),
        new List<ArgumentNode> { new PositionalArgNode(assign.Span, lambda) }
    );

    var unwrapped = MakeUnwrapCall(assign.Span, continueWithCall, userReturnType);

    return new LetExprNode(assign.Span, new List<VarDeclStmtNode> { tempTaskDecl }, unwrapped);
}

private ExprNode MakeAwaitExprChain(UnaryExprNode awaitExpr, IReadOnlyList<StmtNode> after, TypeNode? userReturnType)
{
    // await expr; <after>
    // =>
    // let __aura_taskN = expr;
    // return Unwrap(__aura_taskN.ContinueWith<Task<Ret>>(_ => { await __aura_taskN; <after-task> }))

    var operand = awaitExpr.Operand;

    var tempTaskName = FreshTempName(awaitExpr.Span, "__aura_task");
    var tempTaskDecl = new VarDeclStmtNode(awaitExpr.Span, Mutability.Let, tempTaskName, null, operand);

    var contParamName = FreshTempName(awaitExpr.Span, "__aura_await");
    var contParam = new LambdaParamNode(awaitExpr.Span, contParamName, MakeClrTypeNode(awaitExpr.Span, "System.Threading.Tasks.Task"));

    var awaitSideEffect = new UnaryExprNode(awaitExpr.Span, "await", new NameExprNode(awaitExpr.Span, tempTaskName));

    var restTaskExpr = BuildAsyncTaskExpr(after, userReturnType);

    var lambdaBody = (ExprNode)new SeqExprNode(awaitExpr.Span, new ExprNode[] { awaitSideEffect, restTaskExpr });
    var lambda = new LambdaExprNode(awaitExpr.Span, new List<LambdaParamNode> { contParam }, lambdaBody);

    var contResultType = MakeTaskTypeNode(awaitExpr.Span, userReturnType);

    var continueWithCall = new CallExprNode(
        awaitExpr.Span,
        new MemberAccessExprNode(awaitExpr.Span, new NameExprNode(awaitExpr.Span, tempTaskName), new NameNode(awaitExpr.Span, "ContinueWith"), new List<TypeNode> { contResultType }),
        new List<ArgumentNode> { new PositionalArgNode(awaitExpr.Span, lambda) }
    );

    var unwrapped = MakeUnwrapCall(awaitExpr.Span, continueWithCall, userReturnType);

    return new LetExprNode(awaitExpr.Span, new List<VarDeclStmtNode> { tempTaskDecl }, unwrapped);
}

private ExprNode MakeReturnAwaitTaskExpr(UnaryExprNode awaitExpr, TypeNode userReturnType)
{
    // return await expr;  (non-void)
    // =>
    // let __aura_taskN = expr;
    // return __aura_taskN.ContinueWith<Ret>(_ => await __aura_taskN);

    var operand = awaitExpr.Operand;

    var tempTaskName = FreshTempName(awaitExpr.Span, "__aura_task");
    var tempTaskDecl = new VarDeclStmtNode(awaitExpr.Span, Mutability.Let, tempTaskName, null, operand);

    var contParamName = FreshTempName(awaitExpr.Span, "__aura_await");
    var contParam = new LambdaParamNode(awaitExpr.Span, contParamName, MakeClrTypeNode(awaitExpr.Span, "System.Threading.Tasks.Task"));

    var awaitedValue = new UnaryExprNode(awaitExpr.Span, "await", new NameExprNode(awaitExpr.Span, tempTaskName));

    var lambda = new LambdaExprNode(awaitExpr.Span, new List<LambdaParamNode> { contParam }, awaitedValue);

    var continueWithCall = new CallExprNode(
        awaitExpr.Span,
        new MemberAccessExprNode(awaitExpr.Span, new NameExprNode(awaitExpr.Span, tempTaskName), new NameNode(awaitExpr.Span, "ContinueWith"), new List<TypeNode> { userReturnType }),
        new List<ArgumentNode> { new PositionalArgNode(awaitExpr.Span, lambda) }
    );

    return new LetExprNode(awaitExpr.Span, new List<VarDeclStmtNode> { tempTaskDecl }, continueWithCall);
}

private ExprNode MakeUnwrapCall(SourceSpan span, ExprNode continueWithCall, TypeNode? userReturnType)
{
    var taskExtensions = new NameExprNode(span, new NameNode(span, "System.Threading.Tasks.TaskExtensions"));

    MemberAccessExprNode unwrapMember;
    if (userReturnType is null)
    {
        // TaskExtensions.Unwrap(Task<Task>) -> Task
        unwrapMember = new MemberAccessExprNode(span, taskExtensions, new NameNode(span, "Unwrap"), Array.Empty<TypeNode>());
    }
    else
    {
        // TaskExtensions.Unwrap<T>(Task<Task<T>>) -> Task<T>
        unwrapMember = new MemberAccessExprNode(span, taskExtensions, new NameNode(span, "Unwrap"), new List<TypeNode> { userReturnType });
    }

    return new CallExprNode(
        span,
        unwrapMember,
        new List<ArgumentNode> { new PositionalArgNode(span, continueWithCall) }
    );
}

private static List<StmtNode> StmtToList(StmtNode st)
    => st is BlockStmtNode b ? b.Statements.ToList() : new List<StmtNode> { st };

private static bool ContainsAwaitInStmt(StmtNode st)
{
    switch (st)
    {
        case BlockStmtNode b:
            return b.Statements.Any(ContainsAwaitInStmt);
        case VarDeclStmtNode v:
            return v.Init is not null && ContainsAwait(v.Init);
        case ExprStmtNode es:
            return ContainsAwait(es.Expr);
        case ReturnStmtNode r:
            return r.Value is not null && ContainsAwait(r.Value);
        case IfStmtNode iff:
            return (iff.Condition is not null && ContainsAwait(iff.Condition)) ||
                   ContainsAwaitInStmt(iff.Then) ||
                   (iff.Else is not null && ContainsAwaitInStmt(iff.Else));
        default:
            return false;
    }
}

private static bool ContainsAwait(ExprNode expr)
{
    switch (expr)
    {
        case UnaryExprNode u:
            return u.Op == "await" || ContainsAwait(u.Operand);

        case BinaryExprNode b:
            return ContainsAwait(b.Left) || ContainsAwait(b.Right);

        case AssignmentExprNode a:
            return ContainsAwait(a.Left) || ContainsAwait(a.Right);

        case CallExprNode c:
            if (ContainsAwait(c.Callee)) return true;
            foreach (var arg in c.Args)
            {
                if (arg is PositionalArgNode pa && ContainsAwait(pa.Value))
                    return true;
                if (arg is NamedArgNode na && ContainsAwait(na.Value))
                    return true;
            }
            return false;

        case MemberAccessExprNode ma:
            return ContainsAwait(ma.Target);

        case ConditionalExprNode ce:
            return ContainsAwait(ce.Condition) || ContainsAwait(ce.Then) || ContainsAwait(ce.Else);

        case LetExprNode le:
            foreach (var d in le.Decls)
            {
                if (d.Init is not null && ContainsAwait(d.Init))
                    return true;
            }
            return ContainsAwait(le.Body);

        case SeqExprNode se:
            return se.Exprs.Any(ContainsAwait);

        case TryCatchExprNode tc:
            return ContainsAwait(tc.TryExpr) ||
                   tc.Catches.Any(c => ContainsAwait(c.Body));

        case CastExprNode cast:
            return ContainsAwait(cast.Expr);

        case NewExprNode ne:
            foreach (var arg in ne.Args)
            {
                if (arg is PositionalArgNode pa && ContainsAwait(pa.Value))
                    return true;
                if (arg is NamedArgNode na && ContainsAwait(na.Value))
                    return true;
            }
            return false;

        default:
            return false;
    }
}

private static ExprNode MakeCompletedTaskExpr(SourceSpan span)
    => new MemberAccessExprNode(span,
        new NameExprNode(span, new NameNode(span, "System.Threading.Tasks.Task")),
        new NameNode(span, "CompletedTask"),
        Array.Empty<TypeNode>());

private static ExprNode MakeTaskFromResultExpr(SourceSpan span, TypeNode userReturnType, ExprNode value)
{
    var taskType = new NameExprNode(span, new NameNode(span, "System.Threading.Tasks.Task"));
    var fromResult = new MemberAccessExprNode(span, taskType, new NameNode(span, "FromResult"), new List<TypeNode> { userReturnType });
    return new CallExprNode(span, fromResult, new List<ArgumentNode> { new PositionalArgNode(span, value) });
}

private static ExprNode MakeTaskFromExceptionExpr(SourceSpan span, TypeNode? userReturnType, string message)
{
    var taskType = new NameExprNode(span, new NameNode(span, "System.Threading.Tasks.Task"));

    // We intentionally use the parameterless ctor to avoid string-escaping issues in lowering.
    // The diagnostic message is carried by the lowering diagnostic already.
    var exType = MakeClrTypeNode(span, "System.InvalidOperationException");
    var ex = new NewExprNode(span, exType, Array.Empty<ArgumentNode>());

    MemberAccessExprNode fromEx;
    if (userReturnType is null)
        fromEx = new MemberAccessExprNode(span, taskType, new NameNode(span, "FromException"), Array.Empty<TypeNode>());
    else
        fromEx = new MemberAccessExprNode(span, taskType, new NameNode(span, "FromException"), new List<TypeNode> { userReturnType });

    return new CallExprNode(span, fromEx, new List<ArgumentNode> { new PositionalArgNode(span, ex) });
}

/// <summary>
/// Converts a block of statements into a single expression for use in a lambda body.
/// Extracts VarDecls into LetExprNode, side effects into SeqExprNode,
/// and uses the last return value or a null literal as the final value.
/// </summary>
private static ExprNode MakeBlockAsExpr(BlockStmtNode block, TypeNode? expectedReturnType)
{
    // Simple approach: convert block into a SeqExprNode.
    // For each statement, extract the expression form.
    var exprs = new List<ExprNode>();
    ExprNode? returnValue = null;

    foreach (var st in block.Statements)
    {
        switch (st)
        {
            case ExprStmtNode es:
                exprs.Add(es.Expr);
                break;
            case ReturnStmtNode r:
                if (r.Value is not null)
                    returnValue = r.Value;
                break;
            case VarDeclStmtNode v when v.Init is not null:
                // Side-effect: evaluate initializer
                exprs.Add(v.Init);
                break;
            default:
                // Best-effort: skip non-expression statements
                break;
        }
    }

    if (returnValue is not null)
        exprs.Add(returnValue);
    else if (expectedReturnType is null)
        exprs.Add(new LiteralExprNode(block.Span, LiteralKind.Null, "null"));

    if (exprs.Count == 0)
        return new LiteralExprNode(block.Span, LiteralKind.Null, "null");

    if (exprs.Count == 1)
        return exprs[0];

    return new SeqExprNode(block.Span, exprs);
}

private static NamedTypeNode MakeClrTypeNode(SourceSpan span, string fullName)
    => new NamedTypeNode(span,
        new QualifiedNameNode(span, new List<NameNode> { new NameNode(span, fullName) }),
        Array.Empty<TypeNode>());

private static NamedTypeNode MakeClrTypeNode(SourceSpan span, string fullName, IReadOnlyList<TypeNode> typeArgs)
    => new NamedTypeNode(span,
        new QualifiedNameNode(span, new List<NameNode> { new NameNode(span, fullName) }),
        typeArgs);

private static TypeNode MakeTaskTypeNode(SourceSpan span, TypeNode? userReturnType)
    => userReturnType is null
        ? MakeClrTypeNode(span, "System.Threading.Tasks.Task")
        : MakeClrTypeNode(span, "System.Threading.Tasks.Task", new List<TypeNode> { userReturnType });

private BlockStmtNode LowerBlock(BlockStmtNode block)
    {
        var loweredStatements = LowerStatementList(block.Statements);
        return new BlockStmtNode(block.Span, loweredStatements);
    }

    private IReadOnlyList<StmtNode> LowerStatementList(IReadOnlyList<StmtNode> statements)
    {
        var outList = new List<StmtNode>(statements.Count);

        for (int i = 0; i < statements.Count; i++)
        {
            var s = statements[i];

            // using declaration without explicit body: captures remainder of this statement list
            if (s is UsingStmtNode us && us.Body is null)
            {
                var remainder = statements.Skip(i + 1).ToList();
                var loweredRemainder = LowerStatementList(remainder);
                var implicitBody = new BlockStmtNode(us.Span, loweredRemainder);

                outList.Add(LowerUsingStatement(us, implicitBody));
                return outList; // remainder already consumed into using
            }

            // guard in statement position: exprStmt(GuardExpr)
            if (s is ExprStmtNode es && es.Expr is GuardExprNode g)
            {
                outList.Add(LowerGuardAsStatement(es.Span, g));
                continue;
            }

            outList.Add(LowerStmt(s));
        }

        return outList;
    }

    private StmtNode LowerStmt(StmtNode s) =>
        s switch
        {
            BlockStmtNode b => LowerBlock(b),
            VarDeclStmtNode v => new VarDeclStmtNode(v.Span, v.Mutability, v.Name, v.Type, v.Init is null ? null : LowerExpr(v.Init)),
            ExprStmtNode e => new ExprStmtNode(e.Span, LowerExpr(e.Expr)),
            IfStmtNode i => new IfStmtNode(i.Span, LowerExpr(i.Condition), LowerBlock(i.Then), i.Else is null ? null : LowerStmt(i.Else)),
            WhileStmtNode w => new WhileStmtNode(w.Span, LowerExpr(w.Condition), LowerBlock(w.Body)),
            ForEachStmtNode f => new ForEachStmtNode(f.Span, f.ItemName, LowerExpr(f.Collection), (BlockStmtNode)LowerStmt(f.Body)),
            ReturnStmtNode r => new ReturnStmtNode(r.Span, r.Value is null ? null : LowerExpr(r.Value)),
            ThrowStmtNode t => new ThrowStmtNode(t.Span, t.Value is not null ? LowerExpr(t.Value) : null),
            TryStmtNode t => LowerTry(t),
            SwitchStmtNode sw => LowerSwitchStmt(sw),
            UsingStmtNode us when us.Body is not null => LowerUsingStatement(us, LowerBlock(us.Body)),
            _ => s
        };

    private TryStmtNode LowerTry(TryStmtNode t)
    {
        var tryBlock = LowerBlock(t.TryBlock);

        var catches = new List<CatchClauseNode>(t.Catches.Count);
        foreach (var c in t.Catches)
        {
            catches.Add(new CatchClauseNode(
                c.Span,
                c.Name,
                c.Type,
                LowerBlock(c.Body)
            ));
        }

        var fin = t.Finally is null ? null : LowerBlock(t.Finally);
        return new TryStmtNode(t.Span, tryBlock, catches, fin);
    }

    private SwitchStmtNode LowerSwitchStmt(SwitchStmtNode sw)
    {
        // NOTE: we currently do NOT desugar switch statements into if-chains because of 'break' semantics.
        // We still lower nested expressions and statement lists, and we also lower 'when' expressions.
        var value = LowerExpr(sw.Value);

        var sections = new List<SwitchSectionNode>(sw.Sections.Count);
        foreach (var sec in sw.Sections)
        {
            var labels = new List<SwitchLabelNode>(sec.Labels.Count);
            foreach (var lab in sec.Labels)
            {
                labels.Add(lab switch
                {
                    CaseLabelNode c => new CaseLabelNode(c.Span, LowerPatternNode(c.Pattern), c.WhenGuard is null ? null : LowerExpr(c.WhenGuard)),
                    DefaultLabelNode d => d,
                    _ => lab
                });
            }

            var loweredStatements = LowerStatementList(sec.Statements);
            sections.Add(new SwitchSectionNode(sec.Span, labels, loweredStatements));
        }

        return new SwitchStmtNode(sw.Span, value, sections);
    }

    // -----------------------------
    // Using lowering
    // -----------------------------

    private StmtNode LowerUsingStatement(UsingStmtNode us, BlockStmtNode loweredBody)
    {
        // Lower resource
        var decls = FlattenUsingResource(us.Resource, us.Span);

        // Nest try/finally for multiple resources (C#-like semantics)
        return BuildUsingNest(us.Span, us.Await, decls, loweredBody);
    }

    private sealed record UsingDeclInfo(NameNode Name, TypeNode? Type, ExprNode Init, SourceSpan Span);

    private List<UsingDeclInfo> FlattenUsingResource(UsingResourceNode resource, SourceSpan span)
    {
        var list = new List<UsingDeclInfo>();

        switch (resource)
        {
            case UsingDeclsResourceNode d:
                foreach (var ld in d.Decls)
                {
                    // Lower init expression
                    var init = LowerExpr(ld.Init);
                    list.Add(new UsingDeclInfo(ld.Name, ld.Type, init, ld.Span));
                }
                break;

            case UsingExprResourceNode e:
            {
                var tmpName = FreshTempName(span, "__aura_using");
                var init = LowerExpr(e.Expr);
                list.Add(new UsingDeclInfo(tmpName, null, init, span));
                break;
            }

            default:
                _diags.Add(new LoweringDiagnostic(span, "AURLW1000", LoweringSeverity.Warning,
                    Msg.Diag("AURLW1000", resource.GetType().Name)));
                break;
        }

        return list;
    }

    private StmtNode BuildUsingNest(SourceSpan span, bool isAwait, IReadOnlyList<UsingDeclInfo> decls, BlockStmtNode body)
    {
        if (decls.Count == 0)
            return body;

        // build outer-most first
        StmtNode current = body;

        for (int i = decls.Count - 1; i >= 0; i--)
        {
            var d = decls[i];

            // var/let doesn't matter for IL; use 'var' for temp, but semantic checks happened earlier
            var declStmt = new VarDeclStmtNode(d.Span, Mutability.Var, d.Name, d.Type, d.Init);

            var finallyBlock = BuildDisposeFinallyBlock(span, isAwait, d.Name);

            var tryStmt = new TryStmtNode(span,
                new BlockStmtNode(span, new[] { current }),
                Array.Empty<CatchClauseNode>(),
                finallyBlock
            );

            current = new BlockStmtNode(span, new StmtNode[] { declStmt, tryStmt });
        }

        return current;
    }

    private BlockStmtNode BuildDisposeFinallyBlock(SourceSpan span, bool isAwait, NameNode local)
    {
        // if (local != null) { local.Dispose() }   or await local.DisposeAsync()
        var localExpr = new NameExprNode(local.Span, local);

        var cond = new BinaryExprNode(span, "!=", localExpr, new LiteralExprNode(span, LiteralKind.Null, "null"));

        var disposeMember = isAwait ? "DisposeAsync" : "Dispose";
        var call = new CallExprNode(
            span,
            new MemberAccessExprNode(span, localExpr, new NameNode(local.Span, disposeMember), Array.Empty<TypeNode>()),
            Array.Empty<ArgumentNode>()
        );

        ExprNode callExpr = call;
        if (isAwait)
            callExpr = new UnaryExprNode(span, "await", callExpr);

        var thenBlock = new BlockStmtNode(span, new StmtNode[]
        {
            new ExprStmtNode(span, callExpr)
        });

        return new BlockStmtNode(span, new StmtNode[]
        {
            new IfStmtNode(span, cond, thenBlock, null)
        });
    }

    // -----------------------------
    // Guard (~) lowering
    // -----------------------------

    private StmtNode LowerGuardAsStatement(SourceSpan span, GuardExprNode g)
    {
        var innerStmt = (StmtNode)new ExprStmtNode(span, LowerExpr(g.Expr));

        // Nested try/catch from left to right:
        // expr ~ h1 ~ h2 => try { try { expr } catch(e){ h1(e) } } catch(e){ h2(e) }
        foreach (var handler in g.Handlers)
        {
            var exName = FreshTempName(span, "__aura_ex");
            var exType = MakeNamedType(span, "System", "Exception");

            var handlerExpr = LowerExpr(handler);

            var call = new CallExprNode(
                span,
                handlerExpr,
                new ArgumentNode[]
                {
                    new PositionalArgNode(span, new NameExprNode(exName.Span, exName))
                }
            );

            var catchBody = new BlockStmtNode(span, new StmtNode[]
            {
                new ExprStmtNode(span, call)
            });

            innerStmt = new TryStmtNode(
                span,
                new BlockStmtNode(span, new[] { innerStmt }),
                new[]
                {
                    new CatchClauseNode(span, exName, exType, catchBody)
                },
                null
            );
        }

        return innerStmt;
    }

    /// <summary>
    /// Lowers guard (~) in expression position by rewriting it into nested <see cref="TryCatchExprNode"/>.
    ///
    /// Example:
    /// <code>
    ///   expr ~ h1 ~ h2
    /// </code>
    /// becomes:
    /// <code>
    ///   try { try { expr } catch (Exception e0) { h1(e0) } }
    ///   catch (Exception e1) { h2(e1) }
    /// </code>
    ///
    /// This keeps the construct as an expression (so it works inside let init / call args / interpolation parts).
    /// </summary>
    private ExprNode LowerGuardAsExpression(GuardExprNode g)
    {
        var span = g.Span;

        // First lower the guarded expression and all handlers.
        // Then wrap the expression in nested try/catch from left to right.
        ExprNode inner = LowerExpr(g.Expr);

        foreach (var handler in g.Handlers)
        {
            var exName = FreshTempName(span, "__aura_ex");
            var exType = MakeNamedType(span, "System", "Exception");

            var handlerExpr = LowerExpr(handler);

            // handler(exception)
            var call = new CallExprNode(
                span,
                handlerExpr,
                new ArgumentNode[]
                {
                    new PositionalArgNode(span, new NameExprNode(exName.Span, exName))
                }
            );

            inner = new TryCatchExprNode(
                span,
                TryExpr: inner,
                Catches: new[]
                {
                    new CatchExprClauseNode(span, exType, exName, call)
                }
            );
        }

        return inner;
    }

    // -----------------------------
    // Expressions
    // -----------------------------

    private ExprNode LowerExpr(ExprNode e) =>
        e switch
        {
            PipeExprNode p => LowerPipe(p),
            SwitchExprNode sw => LowerSwitchExpr(sw),
            GuardExprNode g => LowerGuardAsExpression(g),
            LetExprNode le => new LetExprNode(le.Span, le.Decls.Select(LowerVarDeclForLet).ToList(), LowerExpr(le.Body)),

            TryCatchExprNode te => new TryCatchExprNode(te.Span, LowerExpr(te.TryExpr), te.Catches.Select(LowerCatchExprClause).ToList()),

            CallExprNode c => LowerCallExpr(c),
            MemberAccessExprNode m => new MemberAccessExprNode(m.Span, LowerExpr(m.Target), m.Member, m.TypeArgs),
            IndexExprNode i => LowerIndexExpr(i),
            AssignmentExprNode a => LowerAssignmentExpr(a),
            ConditionalExprNode cnd => new ConditionalExprNode(cnd.Span, LowerExpr(cnd.Condition), LowerExpr(cnd.Then), LowerExpr(cnd.Else)),
            BinaryExprNode b => new BinaryExprNode(b.Span, b.Op, LowerExpr(b.Left), LowerExpr(b.Right)),
            UnaryExprNode u => u.Op == "derivateof" ? LowerDerivateof(u) : new UnaryExprNode(u.Span, u.Op, LowerExpr(u.Operand)),
            NewExprNode n => LowerNewExpr(n),
            BuilderNewExprNode bn => new BuilderNewExprNode(bn.Span, LowerExpr(bn.Builder)),
            IsPatternExprNode ip => new IsPatternExprNode(ip.Span, LowerExpr(ip.Expr), LowerPatternNode(ip.Pattern)),
            AsExprNode a => new AsExprNode(a.Span, LowerExpr(a.Expr), a.Type),
            InterpolatedStringExprNode s => new InterpolatedStringExprNode(s.Span, s.Parts.Select(LowerInterpPart).ToList()),
            _ => e
        };

    private CatchExprClauseNode LowerCatchExprClause(CatchExprClauseNode c) =>
        new CatchExprClauseNode(c.Span, c.Type, c.Name, LowerExpr(c.Body));

    private VarDeclStmtNode LowerVarDeclForLet(VarDeclStmtNode v) =>
        new VarDeclStmtNode(v.Span, v.Mutability, v.Name, v.Type, v.Init is null ? null : LowerExpr(v.Init));

    private InterpPartNode LowerInterpPart(InterpPartNode p) =>
        p switch
        {
            InterpExprPartNode e => new InterpExprPartNode(e.Span, LowerExpr(e.Expr)),
            _ => p
        };

    private ArgumentNode LowerArg(ArgumentNode a) =>
        a switch
        {
            PositionalArgNode p => new PositionalArgNode(p.Span, LowerExpr(p.Value)),
            NamedArgNode n => new NamedArgNode(n.Span, n.Name, n.AssignToken, LowerExpr(n.Value)),
            PlaceholderArgNode ph => ph, // keep placeholder
            _ => a
        };

    // ------------------------------
    // v4: derivateof lowering
    // ------------------------------

    /// <summary>
    /// Lowers `derivateof funcName` to access the synthesized op properties.
    /// derivable function lowering creates properties: __aura_op_{fnName}_{opName}
    /// derivateof returns a tuple of those property accessors.
    /// </summary>
    private ExprNode LowerDerivateof(UnaryExprNode u)
    {
        // The operand should be a name referencing the derivable function
        if (u.Operand is not NameExprNode nameRef)
        {
            _diags.Add(new LoweringDiagnostic(u.Span, "LWR5001", LoweringSeverity.Error,
                Msg.Diag("LWR5001")));
            return u;
        }

        // Search current scope for __aura_op_{fnName}_* properties
        // At lowering time we don't have full symbol table, so we emit a MemberAccessExprNode
        // that the codegen can resolve. The derivable lowering guarantees these properties exist.
        var fnName = nameRef.Name.Text;

        // Produce a special marker expression that codegen interprets.
        // Use a CallExprNode to __aura_derivateof_<fnName>() as a marker.
        var marker = new NameExprNode(u.Span, new NameNode(u.Span, $"__aura_derivateof_{fnName}"));
        return new CallExprNode(u.Span, marker, Array.Empty<ArgumentNode>().ToList());
    }

    // ------------------------------
    // v4: Serialization lowering
    // ------------------------------

    /// <summary>
    /// Lowers call expressions with special handling for serialize()/deserialize() calls.
    /// obj.serialize() -> System.Text.Json.JsonSerializer.Serialize(obj)
    /// T.deserialize(data) -> System.Text.Json.JsonSerializer.Deserialize&lt;T&gt;(data)
    /// </summary>
    private ExprNode LowerCallExpr(CallExprNode c)
    {
        // Check for .serialize() pattern: target.serialize()
        if (c.Callee is MemberAccessExprNode ma && ma.Member.Text == "serialize" && c.Args.Count == 0)
        {
            var target = LowerExpr(ma.Target);
            var serializer = new MemberAccessExprNode(c.Span,
                new NameExprNode(c.Span, new NameNode(c.Span, "System.Text.Json.JsonSerializer")),
                new NameNode(c.Span, "Serialize"),
                Array.Empty<TypeNode>());
            return new CallExprNode(c.Span, serializer, new List<ArgumentNode>
            {
                new PositionalArgNode(c.Span, target)
            });
        }

        // Check for T.deserialize(data) pattern: TypeName.deserialize(data)
        if (c.Callee is MemberAccessExprNode ma2 && ma2.Member.Text == "deserialize" && c.Args.Count == 1)
        {
            var typeExpr = ma2.Target;
            var arg = LowerArg(c.Args[0]);

            // Build: System.Text.Json.JsonSerializer.Deserialize<T>(data)
            // Extract type name from the target expression
            var typeName = typeExpr switch
            {
                NameExprNode ne => ne.Name.Text,
                _ => null
            };

            if (typeName is not null)
            {
                var typeNode = MakeNamedType(c.Span, typeName);
                var serializer = new MemberAccessExprNode(c.Span,
                    new NameExprNode(c.Span, new NameNode(c.Span, "System.Text.Json.JsonSerializer")),
                    new NameNode(c.Span, "Deserialize"),
                    new List<TypeNode> { typeNode });
                return new CallExprNode(c.Span, serializer, new List<ArgumentNode> { arg });
            }
        }

        // Default: lower normally
        return new CallExprNode(c.Span, LowerExpr(c.Callee), c.Args.Select(LowerArg).ToList());
    }

    // ------------------------------
    // v4: new T(builder) / [BuildMe] lowering
    // ------------------------------

    /// <summary>
    /// Lowers new expressions. For [BuildMe]-annotated types, validates that the argument
    /// is a builder instance, and transforms to: builder.Build() pattern.
    /// new T(builderInstance) → builderInstance.Build()
    /// </summary>
    private ExprNode LowerNewExpr(NewExprNode n)
    {
        // Check if this is a [BuildMe] type
        var typeName = n.TypeRef switch
        {
            NamedTypeNode nt => nt.Name.ToString(),
            _ => null
        };

        if (typeName is not null && _buildMeTypes.Contains(typeName) && n.Args.Count == 1)
        {
            // new T(builder) → builder.Build()
            var builderArg = n.Args[0] switch
            {
                PositionalArgNode p => LowerExpr(p.Value),
                NamedArgNode na => LowerExpr(na.Value),
                _ => null
            };

            if (builderArg is not null)
            {
                var buildCall = new MemberAccessExprNode(n.Span,
                    builderArg,
                    new NameNode(n.Span, "Build"),
                    Array.Empty<TypeNode>());
                return new CallExprNode(n.Span, buildCall, Array.Empty<ArgumentNode>().ToList());
            }
        }

        // Default: lower args normally
        return new NewExprNode(n.Span, n.TypeRef, n.Args.Select(LowerArg).ToList());
    }

    // ------------------------------
    // v4: Index lowering
    // ------------------------------

    private ExprNode LowerIndexExpr(IndexExprNode ix)
    {
        // 1) Room indexer: Room["Lobby"] => Room.getRoom("Lobby")
        if (ix.Target is NameExprNode ne && ne.Name.Text == "Room")
        {
            var loweredIndex = LowerExpr(ix.Index);
            var callee = new MemberAccessExprNode(ix.Span,
                new NameExprNode(ix.Span, new NameNode(ix.Span, "Room")),
                new NameNode(ix.Span, "getRoom"),
                Array.Empty<TypeNode>());

            return new CallExprNode(ix.Span, callee, new List<ArgumentNode>
            {
                new PositionalArgNode(ix.Span, loweredIndex)
            });
        }

        // 2) Predicate indexer: list[item > 2] => System.Linq.Enumerable.Where(list, x => x > 2)
        if (ContainsItemKeyword(ix.Index))
        {
            var loweredTarget = LowerExpr(ix.Target);
            var loweredPred = LowerExpr(ix.Index);

            var paramName = FreshTempName(ix.Span, "__aura_item");
            var param = new LambdaParamNode(ix.Span, paramName, null);
            var predBody = RewriteItemKeyword(loweredPred, paramName);
            var lambda = new LambdaExprNode(ix.Span, new List<LambdaParamNode> { param }, predBody);

            var callee = new MemberAccessExprNode(ix.Span,
                new NameExprNode(ix.Span, new NameNode(ix.Span, "System.Linq.Enumerable")),
                new NameNode(ix.Span, "Where"),
                Array.Empty<TypeNode>());

            return new CallExprNode(ix.Span, callee, new List<ArgumentNode>
            {
                new PositionalArgNode(ix.Span, loweredTarget),
                new PositionalArgNode(ix.Span, lambda)
            });
        }

        // Default: keep indexing (codegen may add support later).
        return new IndexExprNode(ix.Span, LowerExpr(ix.Target), LowerExpr(ix.Index));
    }

    private static bool ContainsItemKeyword(ExprNode expr)
    {
        return expr switch
        {
            NameExprNode ne => ne.Name.Text == "item",
            MemberAccessExprNode ma => ContainsItemKeyword(ma.Target),
            IndexExprNode ix => ContainsItemKeyword(ix.Target) || ContainsItemKeyword(ix.Index),
            CallExprNode c => ContainsItemKeyword(c.Callee) || c.Args.Any(a => a switch
            {
                PositionalArgNode p => ContainsItemKeyword(p.Value),
                NamedArgNode n => ContainsItemKeyword(n.Value),
                _ => false
            }),
            UnaryExprNode u => ContainsItemKeyword(u.Operand),
            BinaryExprNode b => ContainsItemKeyword(b.Left) || ContainsItemKeyword(b.Right),
            AssignmentExprNode a => ContainsItemKeyword(a.Left) || ContainsItemKeyword(a.Right),
            ConditionalExprNode cnd => ContainsItemKeyword(cnd.Condition) || ContainsItemKeyword(cnd.Then) || ContainsItemKeyword(cnd.Else),
            LetExprNode l => l.Decls.Any(d => d.Init is not null && ContainsItemKeyword(d.Init)) || ContainsItemKeyword(l.Body),
            CastExprNode cst => ContainsItemKeyword(cst.Expr),
            TypeIsExprNode tis => ContainsItemKeyword(tis.Expr),
            LambdaExprNode => false, // don't look into nested lambdas
            _ => false
        };
    }

    private static ExprNode RewriteItemKeyword(ExprNode expr, NameNode replacement)
    {
        return expr switch
        {
            NameExprNode ne when ne.Name.Text == "item" => new NameExprNode(ne.Span, replacement),
            MemberAccessExprNode ma => new MemberAccessExprNode(ma.Span, RewriteItemKeyword(ma.Target, replacement), ma.Member, ma.TypeArgs),
            IndexExprNode ix => new IndexExprNode(ix.Span, RewriteItemKeyword(ix.Target, replacement), RewriteItemKeyword(ix.Index, replacement)),
            CallExprNode c => new CallExprNode(c.Span, RewriteItemKeyword(c.Callee, replacement), c.Args.Select(a => a switch
            {
                PositionalArgNode p => (ArgumentNode)new PositionalArgNode(p.Span, RewriteItemKeyword(p.Value, replacement)),
                NamedArgNode n => new NamedArgNode(n.Span, n.Name, n.AssignToken, RewriteItemKeyword(n.Value, replacement)),
                PlaceholderArgNode ph => ph,
                _ => a
            }).ToList()),
            UnaryExprNode u => new UnaryExprNode(u.Span, u.Op, RewriteItemKeyword(u.Operand, replacement)),
            BinaryExprNode b => new BinaryExprNode(b.Span, b.Op, RewriteItemKeyword(b.Left, replacement), RewriteItemKeyword(b.Right, replacement)),
            AssignmentExprNode a => new AssignmentExprNode(a.Span, a.Op, RewriteItemKeyword(a.Left, replacement), RewriteItemKeyword(a.Right, replacement)),
            ConditionalExprNode cnd => new ConditionalExprNode(cnd.Span, RewriteItemKeyword(cnd.Condition, replacement), RewriteItemKeyword(cnd.Then, replacement), RewriteItemKeyword(cnd.Else, replacement)),
            LetExprNode l => new LetExprNode(l.Span,
                l.Decls.Select(d => new VarDeclStmtNode(d.Span, d.Mutability, d.Name, d.Type, d.Init is null ? null : RewriteItemKeyword(d.Init, replacement))).ToList(),
                RewriteItemKeyword(l.Body, replacement)),
            CastExprNode cst => new CastExprNode(cst.Span, RewriteItemKeyword(cst.Expr, replacement), cst.Type),
            TypeIsExprNode tis => new TypeIsExprNode(tis.Span, RewriteItemKeyword(tis.Expr, replacement), tis.Type),
            LambdaExprNode lam => lam,
            _ => expr
        };
    }

    // ------------------------------
    // v4: Assignment rewriting
    // ------------------------------

    private ExprNode LowerAssignmentExpr(AssignmentExprNode a)
    {
        var left = LowerExpr(a.Left);
        var right = LowerExpr(a.Right);

        // State change sugar: `run = State.Running` inside a type becomes `__aura_state_run = State.Running`.
        if (a.Op == "=" && left is NameExprNode ne && TryGetStateFieldForFunction(ne.Name.Text, out var field))
        {
            left = new NameExprNode(ne.Span, field);
        }

        return new AssignmentExprNode(a.Span, a.Op, left, right);
    }

    private ExprNode LowerPipe(PipeExprNode p)
    {
        // p.Stages[0] | stage1 | stage2 ...
        var current = LowerExpr(p.Stages[0]);

        foreach (var stage in p.Stages.Skip(1))
        {
            current = ApplyPipeStage(current, stage);
        }

        return current;
    }

    private ExprNode ApplyPipeStage(ExprNode value, ExprNode stageExpr)
    {
        // stage can be: callExpr, name/member access (method group), lambda
        var stageLowered = LowerExpr(stageExpr);

        if (stageLowered is CallExprNode call)
        {
            var args = call.Args.ToList();

            var placeholderCount = args.Count(a => a is PlaceholderArgNode);

            // If multiple placeholders exist, wrap in a LetExprNode to evaluate value once.
            if (placeholderCount > 1)
            {
                var tempName = FreshTempName(call.Span, "__aura_pipe");
                var tempDecl = new VarDeclStmtNode(call.Span, Mutability.Let, tempName, null, value);
                var tempRef = new NameExprNode(call.Span, tempName);

                for (int i = 0; i < args.Count; i++)
                {
                    if (args[i] is PlaceholderArgNode)
                        args[i] = new PositionalArgNode(call.Span, tempRef);
                }

                var callExpr = new CallExprNode(call.Span, call.Callee, args);
                return new LetExprNode(call.Span, new List<VarDeclStmtNode> { tempDecl }, callExpr);
            }

            // Single placeholder (or zero): replace all _ with value directly
            var replacedCount = 0;
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] is PlaceholderArgNode)
                {
                    args[i] = new PositionalArgNode(call.Span, value);
                    replacedCount++;
                }
            }

            if (replacedCount == 0)
                args.Insert(0, new PositionalArgNode(call.Span, value));

            return new CallExprNode(call.Span, call.Callee, args);
        }

        // Not a call: treat as method group / function value, call(stage, value)
        return new CallExprNode(stageLowered.Span, stageLowered, new ArgumentNode[]
        {
            new PositionalArgNode(stageLowered.Span, value)
        });
    }

    // -----------------------------
    // Switch expression lowering
    // -----------------------------

    private ExprNode LowerSwitchExpr(SwitchExprNode sw)
    {
        // Evaluate switch value once using LetExprNode temp
        var tempName = FreshTempName(sw.Span, "__aura_sw");
        var loweredValue = LowerExpr(sw.Value);

        var tempDecl = new VarDeclStmtNode(sw.Span, Mutability.Let, tempName, null, loweredValue);

        var valueRef = (ExprNode)new NameExprNode(tempName.Span, tempName);

        // Build nested conditional chain from last arm to first
        ExprNode? chain = null;

        // Find an explicit discard/default arm if present
        // If not present, we'll end with a throw to keep codegen total (semantic should already enforce exhaustiveness).
        var fallback = MakeNonExhaustiveSwitchThrow(sw.Span);

        chain = fallback;

        for (int i = sw.Arms.Count - 1; i >= 0; i--)
        {
            var arm = sw.Arms[i];

            var pat = LowerPatternNode(arm.Pattern);
            var patLower = LowerPatternMatch(valueRef, pat);

            var resultExpr = LowerExpr(arm.Result);

            // If there is a when guard, it must be evaluated only after pattern variables are bound.
            ExprNode thenExpr;

            if (arm.WhenGuard is null)
            {
                thenExpr = WrapBindersIfAny(arm.Span, patLower.Binders, resultExpr);
            }
            else
            {
                var whenExpr = LowerExpr(arm.WhenGuard);

                // if (when) result else next
                var whenChain = new ConditionalExprNode(arm.Span, whenExpr, resultExpr, chain!);
                thenExpr = WrapBindersIfAny(arm.Span, patLower.Binders, whenChain);
            }

            // patternCond ? thenExpr : next
            chain = new ConditionalExprNode(arm.Span, patLower.Condition, thenExpr, chain!);
        }

        return new LetExprNode(sw.Span, new[] { tempDecl }, chain!);
    }

    private ExprNode WrapBindersIfAny(SourceSpan span, IReadOnlyList<VarDeclStmtNode> binders, ExprNode body)
    {
        if (binders.Count == 0)
            return body;

        return new LetExprNode(span, binders, body);
    }

    private sealed record PatternLoweringResult(ExprNode Condition, IReadOnlyList<VarDeclStmtNode> Binders);

    private PatternLoweringResult LowerPatternMatch(ExprNode valueExpr, PatternNode pattern)
    {
        // Returns: condition + binder decls (to be introduced in arm body via LetExpr)
        return pattern switch
        {
            DiscardPatternNode d => new PatternLoweringResult(MakeBoolLiteral(d.Span, true), Array.Empty<VarDeclStmtNode>()),

            VarPatternNode v => LowerVarPattern(valueExpr, v),

            ConstantPatternNode c => new PatternLoweringResult(
                new BinaryExprNode(c.Span, "==", valueExpr, ConstToExpr(c.Span, c.Value)),
                Array.Empty<VarDeclStmtNode>()),

            RelationalPatternNode r => new PatternLoweringResult(
                new BinaryExprNode(r.Span, r.Op, valueExpr, ConstToExpr(r.Span, r.Value)),
                Array.Empty<VarDeclStmtNode>()),

            DeclarationPatternNode d => LowerDeclarationPattern(valueExpr, d),

            TypePatternNode t => LowerTypeOrQualifiedConstantPattern(valueExpr, t),

            ParenthesizedPatternNode p => LowerPatternMatch(valueExpr, p.Inner),

            NotPatternNode n => LowerNotPattern(valueExpr, n),

            AndPatternNode a => LowerAndPattern(valueExpr, a),

            OrPatternNode o => LowerOrPattern(valueExpr, o),

            PropertyPatternNode pp => LowerPropertyPattern(valueExpr, pp),

            ListPatternNode lp => LowerListPattern(valueExpr, lp),

            _ => UnsupportedPattern(valueExpr, pattern)
        };
    }

    private PatternLoweringResult LowerVarPattern(ExprNode valueExpr, VarPatternNode v)
    {
        if (v.Name is null)
            return new PatternLoweringResult(MakeBoolLiteral(v.Span, true), Array.Empty<VarDeclStmtNode>());

        var binder = new VarDeclStmtNode(v.Span, Mutability.Let, v.Name, null, valueExpr);
        return new PatternLoweringResult(MakeBoolLiteral(v.Span, true), new[] { binder });
    }

    private PatternLoweringResult LowerDeclarationPattern(ExprNode valueExpr, DeclarationPatternNode d)
    {
        // cond: value is Type
        var cond = new TypeIsExprNode(d.Span, valueExpr, d.Type);

        // binder: let name: Type = (Type)value
        var cast = new CastExprNode(d.Span, valueExpr, d.Type);
        var binder = new VarDeclStmtNode(d.Span, Mutability.Let, d.Name, d.Type, cast);

        return new PatternLoweringResult(cond, new[] { binder });
    }

    private PatternLoweringResult LowerTypeOrQualifiedConstantPattern(ExprNode valueExpr, TypePatternNode t)
    {
        // Heuristic: if type is a qualified name with more than one part, it might represent an enum member constant
        // (e.g., RobotState.Idle) due to grammar ambiguity. Treat as a constant equality match, but warn.
        if (t.Type is NamedTypeNode nt && nt.Name.Parts.Count > 1 && nt.TypeArgs.Count == 0)
        {
            _diags.Add(new LoweringDiagnostic(t.Span, "AURLW2100", LoweringSeverity.Warning,
                Msg.Diag("AURLW2100")));

            var constExpr = QualifiedNameToExpr(nt.Name);
            return new PatternLoweringResult(
                new BinaryExprNode(t.Span, "==", valueExpr, constExpr),
                Array.Empty<VarDeclStmtNode>());
        }

        // Regular type pattern: value is Type
        return new PatternLoweringResult(
            new TypeIsExprNode(t.Span, valueExpr, t.Type),
            Array.Empty<VarDeclStmtNode>());
    }

    private PatternLoweringResult LowerNotPattern(ExprNode valueExpr, NotPatternNode n)
    {
        var inner = LowerPatternMatch(valueExpr, n.Inner);

        if (inner.Binders.Count > 0)
        {
            _diags.Add(new LoweringDiagnostic(n.Span, "AURLW2001", LoweringSeverity.Error,
                Msg.Diag("AURLW2001")));
        }

        return new PatternLoweringResult(new UnaryExprNode(n.Span, "!", inner.Condition), Array.Empty<VarDeclStmtNode>());
    }

    private PatternLoweringResult LowerAndPattern(ExprNode valueExpr, AndPatternNode a)
    {
        var left = LowerPatternMatch(valueExpr, a.Left);
        var right = LowerPatternMatch(valueExpr, a.Right);

        var merged = MergeBinders(a.Span, left.Binders, right.Binders);

        return new PatternLoweringResult(
            new BinaryExprNode(a.Span, "&&", left.Condition, right.Condition),
            merged);
    }

    private PatternLoweringResult LowerOrPattern(ExprNode valueExpr, OrPatternNode o)
    {
        var left = LowerPatternMatch(valueExpr, o.Left);
        var right = LowerPatternMatch(valueExpr, o.Right);

        if (left.Binders.Count > 0 || right.Binders.Count > 0)
        {
            _diags.Add(new LoweringDiagnostic(o.Span, "AURLW2002", LoweringSeverity.Error,
                Msg.Diag("AURLW2002")));
        }

        return new PatternLoweringResult(
            new BinaryExprNode(o.Span, "||", left.Condition, right.Condition),
            Array.Empty<VarDeclStmtNode>());
    }

    private PatternLoweringResult LowerPropertyPattern(ExprNode valueExpr, PropertyPatternNode pp)
    {
        ExprNode? cond = null;
        var binders = new List<VarDeclStmtNode>();

        foreach (var m in pp.Members)
        {
            var access = new MemberAccessExprNode(m.Span, valueExpr, m.Name, Array.Empty<TypeNode>());
            var sub = LowerPatternMatch(access, m.Pattern);

            cond = cond is null ? sub.Condition : new BinaryExprNode(m.Span, "&&", cond, sub.Condition);

            // Merge binders with duplicate name check
            binders.AddRange(sub.Binders);
        }

        // Check duplicates
        var deduped = DedupBinders(pp.Span, binders);

        return new PatternLoweringResult(cond ?? MakeBoolLiteral(pp.Span, true), deduped);
    }

    private PatternLoweringResult LowerListPattern(ExprNode valueExpr, ListPatternNode lp)
    {
        _diags.Add(new LoweringDiagnostic(lp.Span, "AURLW2003", LoweringSeverity.Error,
            Msg.Diag("AURLW2003")));

        return new PatternLoweringResult(MakeBoolLiteral(lp.Span, false), Array.Empty<VarDeclStmtNode>());
    }

    private PatternLoweringResult UnsupportedPattern(ExprNode valueExpr, PatternNode p)
    {
        _diags.Add(new LoweringDiagnostic(p.Span, "AURLW2099", LoweringSeverity.Error,
            Msg.Diag("AURLW2099", p.GetType().Name)));

        return new PatternLoweringResult(MakeBoolLiteral(p.Span, false), Array.Empty<VarDeclStmtNode>());
    }

    private IReadOnlyList<VarDeclStmtNode> MergeBinders(SourceSpan span, IReadOnlyList<VarDeclStmtNode> a, IReadOnlyList<VarDeclStmtNode> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;

        var merged = new List<VarDeclStmtNode>(a.Count + b.Count);
        merged.AddRange(a);
        merged.AddRange(b);

        return DedupBinders(span, merged);
    }

    private IReadOnlyList<VarDeclStmtNode> DedupBinders(SourceSpan span, List<VarDeclStmtNode> binders)
    {
        var seen = new Dictionary<string, VarDeclStmtNode>(StringComparer.Ordinal);
        var outList = new List<VarDeclStmtNode>();

        foreach (var b in binders)
        {
            var name = b.Name.Text;
            if (seen.TryGetValue(name, out var existing))
            {
                _diags.Add(new LoweringDiagnostic(span, "AURLW2010", LoweringSeverity.Error,
                    Msg.Diag("AURLW2010", name)));
                continue;
            }

            seen[name] = b;
            outList.Add(b);
        }

        return outList;
    }

    private ExprNode ConstToExpr(SourceSpan span, ConstNode c) =>
        c switch
        {
            ConstLiteralNode lit => new LiteralExprNode(lit.Span, lit.Kind, lit.RawText),
            ConstNameNode name => QualifiedNameToExpr(name.Name),
            _ => new LiteralExprNode(span, LiteralKind.Null, "null")
        };

    private ExprNode QualifiedNameToExpr(QualifiedNameNode qn)
    {
        if (qn.Parts.Count == 0)
            return new LiteralExprNode(qn.Span, LiteralKind.Null, "null");

        ExprNode expr = new NameExprNode(qn.Parts[0].Span, qn.Parts[0]);

        for (int i = 1; i < qn.Parts.Count; i++)
        {
            expr = new MemberAccessExprNode(qn.Parts[i].Span, expr, qn.Parts[i], Array.Empty<TypeNode>());
        }

        return expr;
    }

    private ExprNode MakeBoolLiteral(SourceSpan span, bool value) =>
        value
            ? new LiteralExprNode(span, LiteralKind.True, "true")
            : new LiteralExprNode(span, LiteralKind.False, "false");

    private ExprNode MakeNonExhaustiveSwitchThrow(SourceSpan span)
    {
        // throw new System.InvalidOperationException("Non-exhaustive switch expression")
        var exType = MakeNamedType(span, "System", "InvalidOperationException");

        var msg = new LiteralExprNode(span, LiteralKind.String, "\"Non-exhaustive switch expression\"");
        var newEx = new NewExprNode(span, exType, new ArgumentNode[] { new PositionalArgNode(span, msg) });

        return new UnaryExprNode(span, "throw", newEx);
    }

    private NamedTypeNode MakeNamedType(SourceSpan span, params string[] parts)
    {
        var names = parts.Select(p => new NameNode(span, p)).ToList();
        var qn = new QualifiedNameNode(span, names);
        return new NamedTypeNode(span, qn, Array.Empty<TypeNode>());
    }

    private NameNode FreshTempName(SourceSpan span, string prefix)
    {
        var id = _tempId++;
        return new NameNode(span, $"{prefix}{id}");
    }

    // -----------------------------
    // Patterns (generic lowering)
    // -----------------------------

    private PatternNode LowerPatternNode(PatternNode p) =>
        p switch
        {
            NotPatternNode n => new NotPatternNode(n.Span, LowerPatternNode(n.Inner)),
            AndPatternNode a => new AndPatternNode(a.Span, LowerPatternNode(a.Left), LowerPatternNode(a.Right)),
            OrPatternNode o => new OrPatternNode(o.Span, LowerPatternNode(o.Left), LowerPatternNode(o.Right)),
            ParenthesizedPatternNode par => new ParenthesizedPatternNode(par.Span, LowerPatternNode(par.Inner)),
            PropertyPatternNode pp => new PropertyPatternNode(pp.Span, pp.Members.Select(m => new PropertySubpatternNode(m.Span, m.Name, LowerPatternNode(m.Pattern))).ToList()),
            ListPatternNode lp => new ListPatternNode(lp.Span, lp.Items.Select(LowerPatternNode).ToList()),
            _ => p
        };
}
