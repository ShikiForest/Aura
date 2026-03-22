using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using AuraLang.Ast;

public sealed class AuraAstBuilder
{
    /* =========================
     *  Entry
     * ========================= */

    public CompilationUnitNode BuildCompilationUnit(AuraParser.CompilationUnitContext ctx)
    {
        var items = new List<ICompilationItem>();

        foreach (var child in ctx.children ?? Array.Empty<IParseTree>())
        {
            switch (child)
            {
                case AuraParser.ImportDeclContext id:
                    items.Add(BuildImportDecl(id));
                    break;

                case AuraParser.NamespaceDeclContext nd:
                    items.Add(BuildNamespaceDecl(nd));
                    break;

                case AuraParser.TopLevelDeclContext td:
                    items.Add(BuildTopLevelDecl(td));
                    break;

                default:
                    // SEMI / EOF / whitespace 不会出现在 parse tree children 里；这里忽略其他 terminal
                    break;
            }
        }

        return new CompilationUnitNode(SpanFactory.From(ctx), items);
    }

    public ExprNode BuildExpression(AuraParser.ExpressionContext ctx) => BuildExpressionCore(ctx);

    /* =========================
     *  Compilation Items
     * ========================= */

    private ImportDeclNode BuildImportDecl(AuraParser.ImportDeclContext ctx)
        => new(SpanFactory.From(ctx), BuildQualifiedName(ctx.qualifiedName()));

    private NamespaceDeclNode BuildNamespaceDecl(AuraParser.NamespaceDeclContext ctx)
    {
        var name = BuildQualifiedName(ctx.qualifiedName());
        var members = BuildNamespaceBody(ctx.namespaceBody());
        return new NamespaceDeclNode(SpanFactory.From(ctx), name, members);
    }

    private IReadOnlyList<ICompilationItem> BuildNamespaceBody(AuraParser.NamespaceBodyContext ctx)
    {
        var members = new List<ICompilationItem>();

        foreach (var child in ctx.children ?? Array.Empty<IParseTree>())
        {
            switch (child)
            {
                case AuraParser.ImportDeclContext id:
                    members.Add(BuildImportDecl(id));
                    break;
                case AuraParser.TopLevelDeclContext td:
                    members.Add(BuildTopLevelDecl(td));
                    break;
                default:
                    break;
            }
        }

        return members;
    }

    private ICompilationItem BuildTopLevelDecl(AuraParser.TopLevelDeclContext ctx)
    {
        if (ctx.traitDecl() != null) return BuildTraitDecl(ctx.traitDecl());
        if (ctx.classDecl() != null) return BuildClassDecl(ctx.classDecl());
        if (ctx.structDecl() != null) return BuildStructDecl(ctx.structDecl());
        if (ctx.enumDecl() != null) return BuildEnumDecl(ctx.enumDecl());
        if (ctx.windowDecl() != null) return BuildWindowDecl(ctx.windowDecl());
        if (ctx.functionDecl() != null) return BuildFunctionDecl(ctx.functionDecl());

        throw new InvalidOperationException("Unknown topLevelDecl: " + ctx.GetText());
    }

    /* =========================
     *  Decls: trait/class/struct/enum/window/function
     * ========================= */

    private TraitDeclNode BuildTraitDecl(AuraParser.TraitDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var name = BuildName(ctx.identifier());

        var members = new List<FunctionSignatureNode>();
        foreach (var m in ctx.traitBody().traitMember())
            members.Add(BuildFunctionSignature(m.functionSignature()));

        return new TraitDeclNode(SpanFactory.From(ctx), attrs, vis, name, members);
    }

    private ClassDeclNode BuildClassDecl(AuraParser.ClassDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var name = BuildName(ctx.identifier());

        var tparams = ctx.typeParameters() != null ? BuildTypeParameters(ctx.typeParameters()) : Array.Empty<TypeParameterNode>();
        var bases = ctx.typeList() != null ? BuildTypeList(ctx.typeList()) : Array.Empty<TypeNode>();

        var members = BuildClassBody(ctx.classBody());
        return new ClassDeclNode(SpanFactory.From(ctx), attrs, vis, name, tparams, bases, members);
    }

    private StructDeclNode BuildStructDecl(AuraParser.StructDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var name = BuildName(ctx.identifier());

        var tparams = ctx.typeParameters() != null ? BuildTypeParameters(ctx.typeParameters()) : Array.Empty<TypeParameterNode>();
        var bases = ctx.typeList() != null ? BuildTypeList(ctx.typeList()) : Array.Empty<TypeNode>();

        var members = BuildClassBody(ctx.classBody());
        return new StructDeclNode(SpanFactory.From(ctx), attrs, vis, name, tparams, bases, members);
    }

    private IReadOnlyList<ITypeMember> BuildClassBody(AuraParser.ClassBodyContext ctx)
    {
        var members = new List<ITypeMember>();
        foreach (var m in ctx.classMember())
        {
            if (m.fieldDecl() != null) members.Add(BuildFieldDecl(m.fieldDecl()));
            else if (m.propertyDecl() != null) members.Add(BuildPropertyDecl(m.propertyDecl()));
            else if (m.functionDecl() != null) members.Add(BuildFunctionDecl(m.functionDecl()));
            else if (m.operatorDecl() != null) members.Add(BuildOperatorDecl(m.operatorDecl()));
            else if (m.enumDecl() != null) members.Add(BuildEnumDecl(m.enumDecl()));
            else if (m.windowDecl() != null) members.Add(BuildWindowDecl(m.windowDecl()));
            else throw new InvalidOperationException("Unknown classMember: " + m.GetText());
        }
        return members;
    }

    private EnumDeclNode BuildEnumDecl(AuraParser.EnumDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var name = BuildName(ctx.identifier());

        var members = new List<EnumMemberNode>();
        foreach (var em in ctx.enumBody().enumMember())
        {
            var mName = BuildName(em.identifier());
            var value = em.expression() != null ? BuildExpressionCore(em.expression()) : null;
            members.Add(new EnumMemberNode(SpanFactory.From(em), mName, value));
        }

        return new EnumDeclNode(SpanFactory.From(ctx), attrs, vis, name, members);
    }

    private WindowDeclNode BuildWindowDecl(AuraParser.WindowDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var name = BuildName(ctx.identifier());
        var target = BuildTypeReference(ctx.typeReference());

        var members = new List<WindowMemberNode>();
        foreach (var wm in ctx.windowBody().windowMemberDecl())
        {
            var mName = BuildName(wm.identifier());
            var mType = BuildType(wm.type());
            members.Add(new WindowMemberNode(SpanFactory.From(wm), mName, mType));
        }

        return new WindowDeclNode(SpanFactory.From(ctx), attrs, vis, name, target, members);
    }

    private FieldDeclNode BuildFieldDecl(AuraParser.FieldDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var mut = ctx.LET() != null ? Mutability.Let : Mutability.Var;
        var name = BuildName(ctx.identifier());
        var type = ctx.type() != null ? BuildType(ctx.type()) : null;
        var init = ctx.expression() != null ? BuildExpressionCore(ctx.expression()) : null;

        return new FieldDeclNode(SpanFactory.From(ctx), attrs, vis, mut, name, type, init);
    }

    private PropertyDeclNode BuildPropertyDecl(AuraParser.PropertyDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var name = BuildName(ctx.identifier());
        var type = BuildType(ctx.type());

        var accessors = new List<AccessorDeclNode>();
        if (ctx.propertyAccessorBlock() != null)
        {
            foreach (var a in ctx.propertyAccessorBlock().accessorDecl())
                accessors.Add(BuildAccessorDecl(a));
        }

        return new PropertyDeclNode(SpanFactory.From(ctx), attrs, vis, name, type, accessors);
    }

    private AccessorDeclNode BuildAccessorDecl(AuraParser.AccessorDeclContext ctx)
    {
        var kind = ctx.GET() != null ? AccessorKind.Get : AccessorKind.Set;

        AccessorBodyNode? body = null;
        if (ctx.expression() != null)
        {
            var expr = BuildExpressionCore(ctx.expression());
            body = new AccessorExprBodyNode(SpanFactory.From(ctx.expression()), expr);
        }
        else if (ctx.block() != null)
        {
            var block = BuildBlock(ctx.block());
            body = new AccessorBlockBodyNode(SpanFactory.From(ctx.block()), block);
        }

        return new AccessorDeclNode(SpanFactory.From(ctx), kind, body);
    }

    private FunctionDeclNode BuildFunctionDecl(AuraParser.FunctionDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var mods = BuildFunctionModifiers(ctx.functionModifier());
        var name = BuildName(ctx.identifier());

        var tparams = ctx.typeParameters() != null ? BuildTypeParameters(ctx.typeParameters()) : Array.Empty<TypeParameterNode>();
        var parameters = ctx.parameterList() != null ? BuildParameters(ctx.parameterList()) : Array.Empty<ParameterNode>();

        var ret = ctx.functionReturnOrState() != null ? BuildReturnSpec(ctx.functionReturnOrState()) : null;
        var wheres = BuildWhereClauses(ctx.whereClause());

        var body = BuildFunctionBody(ctx.functionBody());

        return new FunctionDeclNode(
            SpanFactory.From(ctx),
            attrs, vis, mods, name,
            tparams, parameters, ret, wheres,
            body
        );
    }

    private OperatorDeclNode BuildOperatorDecl(AuraParser.OperatorDeclContext ctx)
    {
        var attrs = BuildAttributeSections(ctx.attributeSection());
        var vis = BuildVisibility(ctx.visibilityModifier());
        var op = ctx.overloadableOp().GetText();
        var parameters = ctx.parameterList() != null ? BuildParameters(ctx.parameterList()) : Array.Empty<ParameterNode>();
        var ret = ctx.functionReturnOrState() != null ? BuildReturnSpec(ctx.functionReturnOrState()) : null;
        var body = BuildFunctionBody(ctx.functionBody());

        return new OperatorDeclNode(SpanFactory.From(ctx), attrs, vis, op, parameters, ret, body);
    }

    private FunctionSignatureNode BuildFunctionSignature(AuraParser.FunctionSignatureContext ctx)
    {
        var vis = BuildVisibility(ctx.visibilityModifier());
        var mods = BuildFunctionModifiers(ctx.functionModifier());
        var name = BuildName(ctx.identifier());

        var tparams = ctx.typeParameters() != null ? BuildTypeParameters(ctx.typeParameters()) : Array.Empty<TypeParameterNode>();
        var parameters = ctx.parameterList() != null ? BuildParameters(ctx.parameterList()) : Array.Empty<ParameterNode>();

        var ret = ctx.functionReturnOrState() != null ? BuildReturnSpec(ctx.functionReturnOrState()) : null;
        var wheres = BuildWhereClauses(ctx.whereClause());

        return new FunctionSignatureNode(
            SpanFactory.From(ctx),
            vis, mods, name,
            tparams, parameters,
            ret, wheres
        );
    }

    private ReturnSpecNode BuildReturnSpec(AuraParser.FunctionReturnOrStateContext ctx)
    {
        if (ctx.THINARROW() != null)
        {
            var t = BuildType(ctx.type());
            return new ReturnTypeSpecNode(SpanFactory.From(ctx), t);
        }
        else
        {
            var q = BuildQualifiedName(ctx.qualifiedName());
            return new StateSpecNode(SpanFactory.From(ctx), q);
        }
    }

    private FunctionBodyNode BuildFunctionBody(AuraParser.FunctionBodyContext ctx)
    {
        if (ctx.block() != null)
        {
            var b = BuildBlock(ctx.block());
            return new FunctionBlockBodyNode(SpanFactory.From(ctx), b);
        }

        // => expression
        var expr = BuildExpressionCore(ctx.expression());
        return new FunctionExprBodyNode(SpanFactory.From(ctx), expr);
    }

    /* =========================
     *  Statements
     * ========================= */

    private BlockStmtNode BuildBlock(AuraParser.BlockContext ctx)
    {
        var list = new List<StmtNode>();
        foreach (var s in ctx.statement())
            list.Add(BuildStatement(s));
        return new BlockStmtNode(SpanFactory.From(ctx), list);
    }

    private StmtNode BuildStatement(AuraParser.StatementContext ctx)
    {
        if (ctx.variableDecl() != null) return BuildVarDeclStmt(ctx.variableDecl());
        if (ctx.ifStatement() != null) return BuildIfStmt(ctx.ifStatement());
        if (ctx.forStatement() != null) return BuildForStmt(ctx.forStatement());
        if (ctx.whileStatement() != null) return BuildWhileStmt(ctx.whileStatement());
        if (ctx.switchStatement() != null) return BuildSwitchStmt(ctx.switchStatement());
        if (ctx.usingStatement() != null) return BuildUsingStmt(ctx.usingStatement());
        if (ctx.returnStatement() != null) return BuildReturnStmt(ctx.returnStatement());
        if (ctx.breakStatement() != null) return new BreakStmtNode(SpanFactory.From(ctx.breakStatement()));
        if (ctx.continueStatement() != null) return new ContinueStmtNode(SpanFactory.From(ctx.continueStatement()));
        if (ctx.throwStatement() != null) return BuildThrowStmt(ctx.throwStatement());
        if (ctx.tryStatement() != null) return BuildTryStmt(ctx.tryStatement());
        if (ctx.opDeclStatement() != null) return BuildOpDeclStmt(ctx.opDeclStatement());
        if (ctx.expressionStatement() != null) return BuildExprStmt(ctx.expressionStatement());
        if (ctx.block() != null) return BuildBlock(ctx.block());

        // SEMI
        return new EmptyStmtNode(SpanFactory.From(ctx));
    }

    private VarDeclStmtNode BuildVarDeclStmt(AuraParser.VariableDeclContext ctx)
    {
        var mut = ctx.LET() != null ? Mutability.Let : Mutability.Var;
        var name = BuildName(ctx.identifier());
        var type = ctx.type() != null ? BuildType(ctx.type()) : null;
        var init = ctx.expression() != null ? BuildExpressionCore(ctx.expression()) : null;
        return new VarDeclStmtNode(SpanFactory.From(ctx), mut, name, type, init);
    }

    private IfStmtNode BuildIfStmt(AuraParser.IfStatementContext ctx)
    {
        var cond = BuildExpressionCore(ctx.expression());
        var blocks = ctx.GetRuleContexts<AuraParser.BlockContext>();
        var thenBlock = BuildBlock(blocks[0]);

        StmtNode? elseNode = null;
        if (ctx.ELSE() != null)
        {
            if (ctx.ifStatement() != null) elseNode = BuildIfStmt(ctx.ifStatement());
            else if (blocks.Length > 1) elseNode = BuildBlock(blocks[1]);
        }

        return new IfStmtNode(SpanFactory.From(ctx), cond, thenBlock, elseNode);
    }

    private ForEachStmtNode BuildForStmt(AuraParser.ForStatementContext ctx)
    {
        var item = BuildName(ctx.identifier());
        var collection = BuildExpressionCore(ctx.expression());
        var body = BuildBlock(ctx.block());
        return new ForEachStmtNode(SpanFactory.From(ctx), item, collection, body);
    }

    private WhileStmtNode BuildWhileStmt(AuraParser.WhileStatementContext ctx)
    {
        var cond = BuildExpressionCore(ctx.expression());
        var body = BuildBlock(ctx.block());
        return new WhileStmtNode(SpanFactory.From(ctx), cond, body);
    }

    private ReturnStmtNode BuildReturnStmt(AuraParser.ReturnStatementContext ctx)
    {
        var value = ctx.expression() != null ? BuildExpressionCore(ctx.expression()) : null;
        return new ReturnStmtNode(SpanFactory.From(ctx), value);
    }

    private ThrowStmtNode BuildThrowStmt(AuraParser.ThrowStatementContext ctx)
    {
        var value = ctx.expression() != null ? BuildExpressionCore(ctx.expression()) : null;
        return new ThrowStmtNode(SpanFactory.From(ctx), value);
    }

    private ExprStmtNode BuildExprStmt(AuraParser.ExpressionStatementContext ctx)
        => new(SpanFactory.From(ctx), BuildExpressionCore(ctx.expression()));

    private OpDeclStmtNode BuildOpDeclStmt(AuraParser.OpDeclStatementContext ctx)
    {
        var name = BuildName(ctx.identifier());
        var ftype = (FunctionTypeNode)BuildFunctionType(ctx.functionType());
        return new OpDeclStmtNode(SpanFactory.From(ctx), name, ftype);
    }

    private TryStmtNode BuildTryStmt(AuraParser.TryStatementContext ctx)
    {
        var tryBlock = BuildBlock(ctx.block());
        var catches = new List<CatchClauseNode>();
        foreach (var c in ctx.catchClause())
            catches.Add(BuildCatchClause(c));

        var fin = ctx.finallyClause() != null ? BuildBlock(ctx.finallyClause().block()) : null;
        return new TryStmtNode(SpanFactory.From(ctx), tryBlock, catches, fin);
    }

    private CatchClauseNode BuildCatchClause(AuraParser.CatchClauseContext ctx)
    {
        NameNode? name = null;
        TypeNode? type = null;

        if (ctx.identifier() != null)
            name = BuildName(ctx.identifier());
        if (ctx.type() != null)
            type = BuildType(ctx.type());

        var body = BuildBlock(ctx.block());
        return new CatchClauseNode(SpanFactory.From(ctx), name, type, body);
    }

    private SwitchStmtNode BuildSwitchStmt(AuraParser.SwitchStatementContext ctx)
    {
        var value = BuildExpressionCore(ctx.expression());
        var sections = new List<SwitchSectionNode>();
        foreach (var sec in ctx.switchBlock().switchSection())
            sections.Add(BuildSwitchSection(sec));

        return new SwitchStmtNode(SpanFactory.From(ctx), value, sections);
    }

    private SwitchSectionNode BuildSwitchSection(AuraParser.SwitchSectionContext ctx)
    {
        var labels = new List<SwitchLabelNode>();
        foreach (var lb in ctx.switchLabel())
            labels.Add(BuildSwitchLabel(lb));

        var statements = new List<StmtNode>();
        foreach (var st in ctx.statement())
            statements.Add(BuildStatement(st));

        return new SwitchSectionNode(SpanFactory.From(ctx), labels, statements);
    }

    private SwitchLabelNode BuildSwitchLabel(AuraParser.SwitchLabelContext ctx)
    {
        if (ctx.CASE() != null)
        {
            var pat = BuildPattern(ctx.pattern());
            var when = ctx.expression() != null ? BuildExpressionCore(ctx.expression()) : null;
            return new CaseLabelNode(SpanFactory.From(ctx), pat, when);
        }

        return new DefaultLabelNode(SpanFactory.From(ctx));
    }

    private UsingStmtNode BuildUsingStmt(AuraParser.UsingStatementContext ctx)
    {
        var isAwait = ctx.AWAIT() != null;
        var res = BuildUsingResource(ctx.usingResource());
        var body = ctx.block() != null ? BuildBlock(ctx.block()) : null;

        return new UsingStmtNode(SpanFactory.From(ctx), isAwait, res, body);
    }

    private UsingResourceNode BuildUsingResource(AuraParser.UsingResourceContext ctx)
        => BuildUsingResourceInner(ctx.usingResourceInner());

    private UsingResourceNode BuildUsingResourceInner(AuraParser.UsingResourceInnerContext ctx)
    {
        // NOTE: Antlr C# target may generate repeated-rule accessors as arrays;
        // using GetRuleContexts<T>() makes this independent of generated overload shapes.
        var decls = ctx.GetRuleContexts<AuraParser.UsingLocalDeclContext>();
        if (decls.Length > 0)
        {
            var list = new List<UsingLocalDeclNode>();
            foreach (var d in decls)
                list.Add(BuildUsingLocalDecl(d));
            return new UsingDeclsResourceNode(SpanFactory.From(ctx), list);
        }

        return new UsingExprResourceNode(SpanFactory.From(ctx), BuildExpressionCore(ctx.expression()));
    }

    private UsingLocalDeclNode BuildUsingLocalDecl(AuraParser.UsingLocalDeclContext ctx)
    {
        var mut = ctx.LET() != null ? Mutability.Let : Mutability.Var;
        var name = BuildName(ctx.identifier());
        var type = ctx.type() != null ? BuildType(ctx.type()) : null;
        var init = BuildExpressionCore(ctx.expression());
        return new UsingLocalDeclNode(SpanFactory.From(ctx), mut, name, type, init);
    }

    /* =========================
     *  Expressions (full)
     * ========================= */

    private ExprNode BuildExpressionCore(AuraParser.ExpressionContext ctx)
        => BuildAssignment(ctx.assignmentExpression());

    private ExprNode BuildAssignment(AuraParser.AssignmentExpressionContext ctx)
    {
        var left = BuildConditional(ctx.conditionalExpression());
        if (ctx.assignmentOperator() == null) return left;

        var op = ctx.assignmentOperator().GetText();
        var right = BuildAssignment(ctx.assignmentExpression());
        return new AssignmentExprNode(SpanFactory.From(ctx), op, left, right);
    }

    private ExprNode BuildConditional(AuraParser.ConditionalExpressionContext ctx)
    {
        var cond = BuildGuard(ctx.guardExpression());
        if (ctx.QUESTION() == null) return cond;

        // In this rule, 'expression' appears at most once, so generated code often only provides expression().
        var thenExpr = BuildExpressionCore(ctx.expression());
        var elseExpr = BuildConditional(ctx.conditionalExpression()); // 右结合
        return new ConditionalExprNode(SpanFactory.From(ctx), cond, thenExpr, elseExpr);
    }

    private ExprNode BuildGuard(AuraParser.GuardExpressionContext ctx)
    {
        var pipes = ctx.GetRuleContexts<AuraParser.PipeExpressionContext>();
        var first = BuildPipe(pipes[0]);

        if (pipes.Length == 1) return first;

        var handlers = new List<ExprNode>();
        for (int i = 1; i < pipes.Length; i++)
            handlers.Add(BuildPipe(pipes[i]));

        return new GuardExprNode(SpanFactory.From(ctx), first, handlers);
    }

    private ExprNode BuildPipe(AuraParser.PipeExpressionContext ctx)
    {
        var parts = ctx.GetRuleContexts<AuraParser.LambdaExpressionContext>();
        if (parts.Length == 1) return BuildLambdaExpr(parts[0]);

        var stages = new List<ExprNode>(parts.Length);
        foreach (var p in parts)
            stages.Add(BuildLambdaExpr(p));

        return new PipeExprNode(SpanFactory.From(ctx), stages);
    }

    private ExprNode BuildLambdaExpr(AuraParser.LambdaExpressionContext ctx)
    {
        if (ctx.FATARROW() != null)
        {
            var ps = BuildLambdaParameters(ctx.lambdaParameters());
            var body = BuildExpressionCore(ctx.expression());
            return new LambdaExprNode(SpanFactory.From(ctx), ps, body);
        }

        return BuildNullCoalesce(ctx.nullCoalescingExpression());
    }

    private IReadOnlyList<LambdaParamNode> BuildLambdaParameters(AuraParser.LambdaParametersContext ctx)
    {
        var list = new List<LambdaParamNode>();

        // identifier 形式：x => ...
        if (ctx.identifier() != null && ctx.LPAREN() == null)
        {
            var n = BuildName(ctx.identifier());
            list.Add(new LambdaParamNode(SpanFactory.From(ctx), n, null));
            return list;
        }

        // (a: T, b) => ...
        if (ctx.lambdaParameterList() != null)
        {
            foreach (var p in ctx.lambdaParameterList().lambdaParameter())
            {
                var n = BuildName(p.identifier());
                var t = p.type() != null ? BuildType(p.type()) : null;
                list.Add(new LambdaParamNode(SpanFactory.From(p), n, t));
            }
        }

        return list;
    }

    private ExprNode BuildNullCoalesce(AuraParser.NullCoalescingExpressionContext ctx)
    {
        var left = BuildLogicalOr(ctx.logicalOrExpression());
        if (ctx.COALESCE() == null) return left;

        var right = BuildNullCoalesce(ctx.nullCoalescingExpression());
        return new BinaryExprNode(SpanFactory.From(ctx), "??", left, right);
    }

    private ExprNode BuildLogicalOr(AuraParser.LogicalOrExpressionContext ctx)
    {
        var terms = ctx.GetRuleContexts<AuraParser.LogicalAndExpressionContext>();
        var left = BuildLogicalAnd(terms[0]);

        // OROR 不在 alternation 中，C# target 会给 OROR() 列表（顺序与 terms 对齐）
        for (int i = 1; i < terms.Length; i++)
        {
            var op = "||";
            var right = BuildLogicalAnd(terms[i]);
            left = new BinaryExprNode(SpanFactory.From(ctx), op, left, right);
        }

        return left;
    }

    private ExprNode BuildLogicalAnd(AuraParser.LogicalAndExpressionContext ctx)
    {
        var terms = ctx.GetRuleContexts<AuraParser.EqualityExpressionContext>();
        var left = BuildEquality(terms[0]);

        for (int i = 1; i < terms.Length; i++)
        {
            var op = "&&";
            var right = BuildEquality(terms[i]);
            left = new BinaryExprNode(SpanFactory.From(ctx), op, left, right);
        }

        return left;
    }

    private ExprNode BuildEquality(AuraParser.EqualityExpressionContext ctx)
    {
        // relationalExpression ((==|!=) relationalExpression)*
        var rels = ctx.GetRuleContexts<AuraParser.RelationalExpressionContext>();
        var left = BuildRelational(rels[0]);

        for (int i = 1; i < rels.Length; i++)
        {
            var op = ctx.GetChild(2 * i - 1).GetText(); // "==" or "!="
            var right = BuildRelational(rels[i]);
            left = new BinaryExprNode(SpanFactory.From(ctx), op, left, right);
        }

        return left;
    }

    private ExprNode BuildRelational(AuraParser.RelationalExpressionContext ctx)
    {
        // 这条规则混了：比较 / is pattern / as type，所以用 children 顺序扫描最稳
        // relationalExpression : additiveExpression ( (cmp additiveExpression) | (is pattern) | (as type) )*
        var adds = ctx.GetRuleContexts<AuraParser.AdditiveExpressionContext>();
        var left = BuildAdditive(adds[0]);

        // 从 child[1] 开始扫描
        var i = 1;
        while (i < ctx.ChildCount)
        {
            var node = ctx.GetChild(i);

            if (node is ITerminalNode t)
            {
                var tt = t.Symbol.Type;
                var text = t.GetText();

                if (tt == AuraLexer.LT || tt == AuraLexer.GT || tt == AuraLexer.LE || tt == AuraLexer.GE)
                {
                    var rightAdd = (AuraParser.AdditiveExpressionContext)ctx.GetChild(i + 1);
                    var right = BuildAdditive(rightAdd);
                    left = new BinaryExprNode(SpanFactory.From(ctx), text, left, right);
                    i += 2;
                    continue;
                }

                if (tt == AuraLexer.IS)
                {
                    var patCtx = (AuraParser.PatternContext)ctx.GetChild(i + 1);
                    var pat = BuildPattern(patCtx);
                    left = new IsPatternExprNode(SpanFactory.From(ctx), left, pat);
                    i += 2;
                    continue;
                }

                if (tt == AuraLexer.AS)
                {
                    var typeCtx = (AuraParser.TypeContext)ctx.GetChild(i + 1);
                    var type = BuildType(typeCtx);
                    left = new AsExprNode(SpanFactory.From(ctx), left, type);
                    i += 2;
                    continue;
                }
            }

            // 不应该到这；防御：跳过
            i++;
        }

        return left;
    }

    private ExprNode BuildAdditive(AuraParser.AdditiveExpressionContext ctx)
    {
        var terms = ctx.GetRuleContexts<AuraParser.MultiplicativeExpressionContext>();
        var left = BuildMultiplicative(terms[0]);

        for (int i = 1; i < terms.Length; i++)
        {
            var op = ctx.GetChild(2 * i - 1).GetText(); // "+" or "-"
            var right = BuildMultiplicative(terms[i]);
            left = new BinaryExprNode(SpanFactory.From(ctx), op, left, right);
        }

        return left;
    }

    private ExprNode BuildMultiplicative(AuraParser.MultiplicativeExpressionContext ctx)
    {
        var terms = ctx.GetRuleContexts<AuraParser.SwitchExpressionContext>();
        var left = BuildSwitchExprOrUnary(terms[0]);

        for (int i = 1; i < terms.Length; i++)
        {
            var op = ctx.GetChild(2 * i - 1).GetText(); // "* / %"
            var right = BuildSwitchExprOrUnary(terms[i]);
            left = new BinaryExprNode(SpanFactory.From(ctx), op, left, right);
        }

        return left;
    }

    private ExprNode BuildSwitchExprOrUnary(AuraParser.SwitchExpressionContext ctx)
    {
        var value = BuildUnary(ctx.unaryExpression());
        if (ctx.switchExpressionBlock() == null) return value;

        var arms = new List<SwitchArmNode>();
        foreach (var a in ctx.switchExpressionBlock().switchExpressionArm())
            arms.Add(BuildSwitchArm(a));

        return new SwitchExprNode(SpanFactory.From(ctx), value, arms);
    }

    private SwitchArmNode BuildSwitchArm(AuraParser.SwitchExpressionArmContext ctx)
    {
        var pat = BuildPattern(ctx.pattern());

        ExprNode? when = null;
        ExprNode result;

        // WHEN 可选：expression 个数可能是 1 或 2
        var exprs = ctx.GetRuleContexts<AuraParser.ExpressionContext>();
        if (ctx.WHEN() != null)
        {
            when = BuildExpressionCore(exprs[0]);
            result = BuildExpressionCore(exprs[1]);
        }
        else
        {
            result = BuildExpressionCore(exprs[0]);
        }

        return new SwitchArmNode(SpanFactory.From(ctx), pat, when, result);
    }

    private ExprNode BuildUnary(AuraParser.UnaryExpressionContext ctx)
    {
        if (ctx.postfixExpression() != null)
            return BuildPostfix(ctx.postfixExpression());

        // prefix operators
        if (ctx.PLUS() != null) return new UnaryExprNode(SpanFactory.From(ctx), "+", BuildUnary(ctx.unaryExpression()));
        if (ctx.MINUS() != null) return new UnaryExprNode(SpanFactory.From(ctx), "-", BuildUnary(ctx.unaryExpression()));
        if (ctx.BANG() != null) return new UnaryExprNode(SpanFactory.From(ctx), "!", BuildUnary(ctx.unaryExpression()));
        if (ctx.AWAIT() != null) return new UnaryExprNode(SpanFactory.From(ctx), "await", BuildUnary(ctx.unaryExpression()));
        if (ctx.THROW() != null) return new UnaryExprNode(SpanFactory.From(ctx), "throw", BuildUnary(ctx.unaryExpression()));
        if (ctx.DERIVATEOF() != null) return new UnaryExprNode(SpanFactory.From(ctx), "derivateof", BuildUnary(ctx.unaryExpression()));

        throw new InvalidOperationException("Unknown unaryExpression: " + ctx.GetText());
    }

    private ExprNode BuildPostfix(AuraParser.PostfixExpressionContext ctx)
    {
        var expr = BuildPrimary(ctx.primaryExpression());

        foreach (var suf in ctx.postfixSuffix())
        {
            // call
            if (suf.LPAREN() != null)
            {
                var args = suf.argumentList() != null ? BuildArguments(suf.argumentList()) : Array.Empty<ArgumentNode>();
                expr = new CallExprNode(SpanFactory.From(suf), expr, args);
                continue;
            }

            // indexer (predicate indexer 也是这里：list[ item > 2 ])
            if (suf.LBRACK() != null)
            {
                var index = BuildExpressionCore(suf.expression());
                expr = new IndexExprNode(SpanFactory.From(suf), expr, index);
                continue;
            }

            // member access
            if (suf.DOT() != null)
            {
                var mem = BuildName(suf.identifier());
                var typeArgs = (suf.typeArguments() != null && suf.typeArguments().typeList() != null)
                    ? BuildTypeList(suf.typeArguments().typeList())
                    : Array.Empty<TypeNode>();
                expr = new MemberAccessExprNode(SpanFactory.From(suf), expr, mem, typeArgs);
                continue;
            }

            throw new InvalidOperationException("Unknown postfixSuffix: " + suf.GetText());
        }

        return expr;
    }

    private ExprNode BuildPrimary(AuraParser.PrimaryExpressionContext ctx)
    {
        if (ctx.literal() != null) return BuildLiteral(ctx.literal());
        if (ctx.identifier() != null) return new NameExprNode(SpanFactory.From(ctx), BuildName(ctx.identifier()));
        if (ctx.expression() != null) return BuildExpressionCore(ctx.expression());
        if (ctx.listLiteral() != null) return BuildListLiteral(ctx.listLiteral());
        if (ctx.newExpression() != null) return BuildNew(ctx.newExpression());

        throw new InvalidOperationException("Unknown primaryExpression: " + ctx.GetText());
    }

    private ExprNode BuildNew(AuraParser.NewExpressionContext ctx)
    {
        // Builder-based new: new(expr) — no typeReference
        if (ctx.typeReference() == null && ctx.expression() != null)
        {
            var builderExpr = BuildExpressionCore(ctx.expression());
            return new BuilderNewExprNode(SpanFactory.From(ctx), builderExpr);
        }

        var typeRef = BuildTypeReference(ctx.typeReference());
        var args = ctx.argumentList() != null ? BuildArguments(ctx.argumentList()) : Array.Empty<ArgumentNode>();
        return new NewExprNode(SpanFactory.From(ctx), typeRef, args);
    }

    private ExprNode BuildListLiteral(AuraParser.ListLiteralContext ctx)
    {
        var items = new List<ExprNode>();
        foreach (var e in ctx.expression())
            items.Add(BuildExpressionCore(e));
        return new ListLiteralExprNode(SpanFactory.From(ctx), items);
    }

    private IReadOnlyList<ArgumentNode> BuildArguments(AuraParser.ArgumentListContext ctx)
    {
        var list = new List<ArgumentNode>();
        foreach (var a in ctx.argument())
            list.Add(BuildArgument(a));
        return list;
    }

    private ArgumentNode BuildArgument(AuraParser.ArgumentContext ctx)
    {
        if (ctx.UNDERSCORE() != null)
            return new PlaceholderArgNode(SpanFactory.From(ctx));

        // named arg: identifier (=|:) expression
        if (ctx.identifier() != null)
        {
            var name = BuildName(ctx.identifier());
            var assignToken = ctx.ASSIGN() != null ? "=" : ":";
            var value = BuildExpressionCore(ctx.expression());
            return new NamedArgNode(SpanFactory.From(ctx), name, assignToken, value);
        }

        // positional
        return new PositionalArgNode(SpanFactory.From(ctx), BuildExpressionCore(ctx.expression()));
    }

    private ExprNode BuildLiteral(AuraParser.LiteralContext ctx)
    {
        if (ctx.NULL() != null) return new LiteralExprNode(SpanFactory.From(ctx), LiteralKind.Null, "null");
        if (ctx.TRUE() != null) return new LiteralExprNode(SpanFactory.From(ctx), LiteralKind.True, "true");
        if (ctx.FALSE() != null) return new LiteralExprNode(SpanFactory.From(ctx), LiteralKind.False, "false");
        if (ctx.INT_LIT() != null) return new LiteralExprNode(SpanFactory.From(ctx), LiteralKind.Int, ctx.INT_LIT().GetText());
        if (ctx.FLOAT_LIT() != null) return new LiteralExprNode(SpanFactory.From(ctx), LiteralKind.Float, ctx.FLOAT_LIT().GetText());
        if (ctx.CHAR_LIT() != null) return new LiteralExprNode(SpanFactory.From(ctx), LiteralKind.Char, ctx.CHAR_LIT().GetText());

        if (ctx.stringLiteral() != null) return BuildStringLiteral(ctx.stringLiteral());

        throw new InvalidOperationException("Unknown literal: " + ctx.GetText());
    }

    private ExprNode BuildStringLiteral(AuraParser.StringLiteralContext ctx)
    {
        if (ctx.STRING_LIT() != null)
            return new LiteralExprNode(SpanFactory.From(ctx), LiteralKind.String, ctx.STRING_LIT().GetText());

        // interpolated
        return BuildInterpolatedString(ctx.interpolatedString());
    }

    private ExprNode BuildInterpolatedString(AuraParser.InterpolatedStringContext ctx)
    {
        var parts = new List<InterpPartNode>();
        foreach (var p in ctx.interpolatedStringPart())
        {
            if (p.INTERP_TEXT() != null)
            {
                parts.Add(new InterpTextPartNode(SpanFactory.From(p), p.INTERP_TEXT().GetText()));
            }
            else
            {
                var expr = BuildExpressionCore(p.expression());
                parts.Add(new InterpExprPartNode(SpanFactory.From(p), expr));
            }
        }

        return new InterpolatedStringExprNode(SpanFactory.From(ctx), parts);
    }

    /* =========================
     *  Patterns & Const
     * ========================= */

    private PatternNode BuildPattern(AuraParser.PatternContext ctx)
        => BuildPatternOr(ctx.patternOr());

    private PatternNode BuildPatternOr(AuraParser.PatternOrContext ctx)
    {
        var parts = ctx.GetRuleContexts<AuraParser.PatternAndContext>();
        var left = BuildPatternAnd(parts[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            var right = BuildPatternAnd(parts[i]);
            left = new OrPatternNode(SpanFactory.From(ctx), left, right);
        }
        return left;
    }

    private PatternNode BuildPatternAnd(AuraParser.PatternAndContext ctx)
    {
        var parts = ctx.GetRuleContexts<AuraParser.PatternNotContext>();
        var left = BuildPatternNot(parts[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            var right = BuildPatternNot(parts[i]);
            left = new AndPatternNode(SpanFactory.From(ctx), left, right);
        }
        return left;
    }

    private PatternNode BuildPatternNot(AuraParser.PatternNotContext ctx)
    {
        if (ctx.PAT_NOT() != null)
            return new NotPatternNode(SpanFactory.From(ctx), BuildPatternNot(ctx.patternNot()));

        return BuildPrimaryPattern(ctx.primaryPattern());
    }

    private PatternNode BuildPrimaryPattern(AuraParser.PrimaryPatternContext ctx)
    {
        // 按首 token 判断分支最稳
        var first = ctx.GetChild(0);

        if (first is ITerminalNode t)
        {
            var tt = t.Symbol.Type;

            if (tt == AuraLexer.UNDERSCORE)
                return new DiscardPatternNode(SpanFactory.From(ctx));

            if (tt == AuraLexer.VAR)
            {
                var name = ctx.identifier() != null ? BuildName(ctx.identifier()) : null;
                return new VarPatternNode(SpanFactory.From(ctx), name);
            }

            if (tt == AuraLexer.LPAREN)
            {
                var inner = BuildPattern(ctx.pattern());
                return new ParenthesizedPatternNode(SpanFactory.From(ctx), inner);
            }

            if (tt == AuraLexer.LBRACE)
            {
                var members = new List<PropertySubpatternNode>();
                if (ctx.propertySubpatternList() != null)
                {
                    foreach (var m in ctx.propertySubpatternList().propertySubpattern())
                    {
                        var n = BuildName(m.identifier());
                        var p = BuildPattern(m.pattern());
                        members.Add(new PropertySubpatternNode(SpanFactory.From(m), n, p));
                    }
                }
                return new PropertyPatternNode(SpanFactory.From(ctx), members);
            }

            if (tt == AuraLexer.LBRACK)
            {
                var items = new List<PatternNode>();
                if (ctx.patternList() != null)
                {
                    foreach (var p in ctx.patternList().pattern())
                        items.Add(BuildPattern(p));
                }
                return new ListPatternNode(SpanFactory.From(ctx), items);
            }

            if (tt == AuraLexer.LT || tt == AuraLexer.LE || tt == AuraLexer.GT || tt == AuraLexer.GE)
            {
                var op = t.GetText();
                var c = BuildConst(ctx.constantExpression());
                return new RelationalPatternNode(SpanFactory.From(ctx), op, c);
            }
        }

        // type patterns / declaration patterns
        if (ctx.typeReference() != null)
        {
            var type = BuildTypeReference(ctx.typeReference());
            if (ctx.identifier() != null)
            {
                var name = BuildName(ctx.identifier());
                return new DeclarationPatternNode(SpanFactory.From(ctx), type, name);
            }
            return new TypePatternNode(SpanFactory.From(ctx), type);
        }

        // constant pattern
        var cn = BuildConst(ctx.constantExpression());
        return new ConstantPatternNode(SpanFactory.From(ctx), cn);
    }

    private ConstNode BuildConst(AuraParser.ConstantExpressionContext ctx)
    {
        if (ctx.constLiteral() != null) return BuildConstLiteral(ctx.constLiteral());
        if (ctx.qualifiedName() != null) return new ConstNameNode(SpanFactory.From(ctx), BuildQualifiedName(ctx.qualifiedName()));

        if (ctx.PLUS() != null) return new ConstUnaryNode(SpanFactory.From(ctx), "+", BuildConst(ctx.constantExpression()));
        if (ctx.MINUS() != null) return new ConstUnaryNode(SpanFactory.From(ctx), "-", BuildConst(ctx.constantExpression()));

        throw new InvalidOperationException("Unknown constantExpression: " + ctx.GetText());
    }

    private ConstNode BuildConstLiteral(AuraParser.ConstLiteralContext ctx)
    {
        if (ctx.NULL() != null) return new ConstLiteralNode(SpanFactory.From(ctx), LiteralKind.Null, "null");
        if (ctx.TRUE() != null) return new ConstLiteralNode(SpanFactory.From(ctx), LiteralKind.True, "true");
        if (ctx.FALSE() != null) return new ConstLiteralNode(SpanFactory.From(ctx), LiteralKind.False, "false");
        if (ctx.INT_LIT() != null) return new ConstLiteralNode(SpanFactory.From(ctx), LiteralKind.Int, ctx.INT_LIT().GetText());
        if (ctx.FLOAT_LIT() != null) return new ConstLiteralNode(SpanFactory.From(ctx), LiteralKind.Float, ctx.FLOAT_LIT().GetText());
        if (ctx.STRING_LIT() != null) return new ConstLiteralNode(SpanFactory.From(ctx), LiteralKind.String, ctx.STRING_LIT().GetText());
        if (ctx.CHAR_LIT() != null) return new ConstLiteralNode(SpanFactory.From(ctx), LiteralKind.Char, ctx.CHAR_LIT().GetText());

        throw new InvalidOperationException("Unknown constLiteral: " + ctx.GetText());
    }

    /* =========================
     *  Types
     * ========================= */

    private TypeNode BuildType(AuraParser.TypeContext ctx)
    {
        TypeNode core;

        if (ctx.functionType() != null) core = BuildFunctionType(ctx.functionType());
        else if (ctx.windowOfType() != null) core = BuildWindowOfType(ctx.windowOfType());
        else core = BuildNamedType(ctx.namedType());

        if (ctx.nullableSuffix() != null)
            core = new NullableTypeNode(SpanFactory.From(ctx), core);

        return core;
    }

    private TypeNode BuildTypeReference(AuraParser.TypeReferenceContext ctx)
        => BuildNamedType(ctx.namedType());

    private TypeNode BuildNamedType(AuraParser.NamedTypeContext ctx)
    {
        if (ctx.builtinType() != null) return BuildBuiltinType(ctx.builtinType());

        var q = BuildQualifiedName(ctx.qualifiedName());
        var args = new List<TypeNode>();
        if (ctx.typeArguments() != null && ctx.typeArguments().typeList() != null)
            args.AddRange(BuildTypeList(ctx.typeArguments().typeList()));

        return new NamedTypeNode(SpanFactory.From(ctx), q, args);
    }

    private BuiltinTypeNode BuildBuiltinType(AuraParser.BuiltinTypeContext ctx)
    {
        var kind = ctx.Start.Type switch
        {
            AuraLexer.I8 => BuiltinTypeKind.I8,
            AuraLexer.I16 => BuiltinTypeKind.I16,
            AuraLexer.I32 => BuiltinTypeKind.I32,
            AuraLexer.I64 => BuiltinTypeKind.I64,
            AuraLexer.U8 => BuiltinTypeKind.U8,
            AuraLexer.U16 => BuiltinTypeKind.U16,
            AuraLexer.U32 => BuiltinTypeKind.U32,
            AuraLexer.U64 => BuiltinTypeKind.U64,
            AuraLexer.F32 => BuiltinTypeKind.F32,
            AuraLexer.F64 => BuiltinTypeKind.F64,
            AuraLexer.DECIMAL_T => BuiltinTypeKind.Decimal,
            AuraLexer.BOOL_T => BuiltinTypeKind.Bool,
            AuraLexer.CHAR_T => BuiltinTypeKind.Char,
            AuraLexer.STRING_T => BuiltinTypeKind.String,
            AuraLexer.OBJECT_T => BuiltinTypeKind.Object,
            AuraLexer.VOID_T => BuiltinTypeKind.Void,
            AuraLexer.HANDLE => BuiltinTypeKind.Handle,
            _ => throw new InvalidOperationException("Unknown builtin type: " + ctx.GetText())
        };

        return new BuiltinTypeNode(SpanFactory.From(ctx), kind);
    }

    private TypeNode BuildFunctionType(AuraParser.FunctionTypeContext ctx)
    {
        var ps = new List<TypeNode>();
        if (ctx.typeList() != null)
            ps.AddRange(BuildTypeList(ctx.typeList()));

        var ret = BuildType(ctx.type());
        return new FunctionTypeNode(SpanFactory.From(ctx), ps, ret);
    }

    private TypeNode BuildWindowOfType(AuraParser.WindowOfTypeContext ctx)
        => new WindowOfTypeNode(SpanFactory.From(ctx), BuildType(ctx.type()));

    private IReadOnlyList<TypeNode> BuildTypeList(AuraParser.TypeListContext ctx)
    {
        var list = new List<TypeNode>();
        foreach (var t in ctx.type())
            list.Add(BuildType(t));
        return list;
    }

    private IReadOnlyList<TypeParameterNode> BuildTypeParameters(AuraParser.TypeParametersContext ctx)
    {
        var list = new List<TypeParameterNode>();
        foreach (var tp in ctx.typeParameter())
        {
            var n = BuildName(tp.identifier());
            list.Add(new TypeParameterNode(SpanFactory.From(tp), n));
        }
        return list;
    }

    private IReadOnlyList<WhereClauseNode> BuildWhereClauses(IList<AuraParser.WhereClauseContext>? clauses)
    {
        if (clauses == null || clauses.Count == 0) return Array.Empty<WhereClauseNode>();

        var list = new List<WhereClauseNode>();
        foreach (var w in clauses)
        {
            var name = BuildName(w.identifier());
            var cons = new List<TypeConstraintNode>();
            foreach (var c in w.constraintList().typeConstraint())
                cons.Add(BuildTypeConstraint(c));

            list.Add(new WhereClauseNode(SpanFactory.From(w), name, cons));
        }
        return list;
    }

    private TypeConstraintNode BuildTypeConstraint(AuraParser.TypeConstraintContext ctx)
    {
        if (ctx.typeReference() != null)
            return new TypeRefConstraintNode(SpanFactory.From(ctx), BuildTypeReference(ctx.typeReference()));

        if (ctx.NEW() != null)
            return new NewConstraintNode(SpanFactory.From(ctx));

        if (ctx.CLASS() != null)
            return new ClassConstraintNode(SpanFactory.From(ctx));

        if (ctx.STRUCT() != null)
            return new StructConstraintNode(SpanFactory.From(ctx));

        throw new InvalidOperationException("Unknown typeConstraint: " + ctx.GetText());
    }

    private IReadOnlyList<ParameterNode> BuildParameters(AuraParser.ParameterListContext ctx)
    {
        var list = new List<ParameterNode>();
        foreach (var p in ctx.parameter())
        {
            var name = BuildName(p.identifier());
            var type = p.type() != null ? BuildType(p.type()) : null;
            var def = p.expression() != null ? BuildExpressionCore(p.expression()) : null;
            list.Add(new ParameterNode(SpanFactory.From(p), name, type, def));
        }
        return list;
    }

    /* =========================
     *  Attributes
     * ========================= */

    private IReadOnlyList<AttributeSectionNode> BuildAttributeSections(IList<AuraParser.AttributeSectionContext>? sections)
    {
        if (sections == null || sections.Count == 0) return Array.Empty<AttributeSectionNode>();

        var list = new List<AttributeSectionNode>();
        foreach (var s in sections)
            list.Add(BuildAttributeSection(s));
        return list;
    }

    private AttributeSectionNode BuildAttributeSection(AuraParser.AttributeSectionContext ctx)
    {
        var attrs = new List<AttributeNode>();
        foreach (var a in ctx.attribute())
            attrs.Add(BuildAttribute(a));
        return new AttributeSectionNode(SpanFactory.From(ctx), attrs);
    }

    private AttributeNode BuildAttribute(AuraParser.AttributeContext ctx)
    {
        var name = BuildQualifiedName(ctx.qualifiedName());
        var args = new List<AttributeArgNode>();

        if (ctx.attributeArgumentList() != null)
        {
            foreach (var arg in ctx.attributeArgumentList().attributeArgument())
                args.Add(BuildAttributeArg(arg));
        }

        return new AttributeNode(SpanFactory.From(ctx), name, args);
    }

    private AttributeArgNode BuildAttributeArg(AuraParser.AttributeArgumentContext ctx)
    {
        // named: identifier (=|:) expression
        if (ctx.identifier() != null)
        {
            var name = BuildName(ctx.identifier());
            var assign = ctx.ASSIGN() != null ? "=" : ":";
            var val = BuildExpressionCore(ctx.expression());
            return new AttributeNamedArgNode(SpanFactory.From(ctx), name, assign, val);
        }

        // positional: expression
        return new AttributePositionalArgNode(SpanFactory.From(ctx), BuildExpressionCore(ctx.expression()));
    }

    /* =========================
     *  Helpers
     * ========================= */

    private Visibility BuildVisibility(AuraParser.VisibilityModifierContext? ctx)
        => ctx != null ? Visibility.Public : Visibility.Default;

    private IReadOnlyList<FunctionModifier> BuildFunctionModifiers(IList<AuraParser.FunctionModifierContext>? mods)
    {
        if (mods == null || mods.Count == 0) return Array.Empty<FunctionModifier>();

        var list = new List<FunctionModifier>();
        foreach (var m in mods)
        {
            if (m.ASYNC() != null) list.Add(FunctionModifier.Async);
            else if (m.DERIVABLE() != null) list.Add(FunctionModifier.Derivable);
        }
        return list;
    }

    private NameNode BuildName(AuraParser.IdentifierContext ctx)
        => new(SpanFactory.From(ctx), ctx.GetText());

    private QualifiedNameNode BuildQualifiedName(AuraParser.QualifiedNameContext ctx)
    {
        var parts = new List<NameNode>();
        foreach (var id in ctx.identifier())
            parts.Add(BuildName(id));

        return new QualifiedNameNode(SpanFactory.From(ctx), parts);
    }
}
