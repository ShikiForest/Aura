using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using PA = Mono.Cecil.ParameterAttributes;
using FA = Mono.Cecil.FieldAttributes;
using AuraLang.Ast;
using AuraLang.I18n;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace AuraLang.CodeGen;

internal sealed class CecilEmitter
{
    private readonly ModuleDefinition _module;
    private readonly TypeDefinition _auraModule;
    private readonly MethodDefinition _method;
    internal readonly ILProcessor _il;
    private readonly HashSet<string> _imports;
    private readonly Dictionary<string, TypeReference> _genericContext;
    private readonly IReadOnlyDictionary<string, MethodDefinition> _topLevelMethods;
    private readonly IReadOnlyDictionary<string, TypeDefinition> _userTypes;
    private readonly List<CodeGenDiagnostic> _diags;

    private Scope _scope;
    internal readonly Dictionary<string, ParameterDefinition> _args = new(StringComparer.Ordinal);

    // Closure support (v2+)
    private readonly Dictionary<string, FieldReference>? _closureFields; // captured name -> field on "this"
    private readonly bool _isLambdaDisplayInstanceMethod;

    // v3: instance method context
    private readonly bool _isInstanceMethod;
    private readonly TypeDefinition _declaringType;

    private int _tempId = 0;
    private int _displayClassId = 0;

    // PDB debug support
    private readonly Mono.Cecil.Cil.Document? _debugDoc;

    public CecilEmitter(
        ModuleDefinition module,
        TypeDefinition auraModule,
        MethodDefinition method,
        HashSet<string> imports,
        IReadOnlyDictionary<string, MethodDefinition> topLevelMethods,
        IReadOnlyDictionary<string, TypeDefinition> userTypes,
        List<CodeGenDiagnostic> diags,
        Dictionary<string, FieldReference>? closureFields = null,
        bool isLambdaDisplayInstanceMethod = false,
        Mono.Cecil.Cil.Document? debugDocument = null)
    {
        _module = module;
        _auraModule = auraModule;
        _method = method;
        _method.Body.InitLocals = true;
        _il = method.Body.GetILProcessor();
        _imports = imports;
        _topLevelMethods = topLevelMethods;
        _userTypes = userTypes;
        _diags = diags;
        _debugDoc = debugDocument;

        _closureFields = closureFields;
        _isLambdaDisplayInstanceMethod = isLambdaDisplayInstanceMethod;

        _declaringType = method.DeclaringType;
        _isInstanceMethod = !method.IsStatic; // includes class instance methods and display-class methods

        _scope = new Scope(parent: null);

        _genericContext = new Dictionary<string, TypeReference>(StringComparer.Ordinal);
        foreach (var gp in _declaringType.GenericParameters)
            _genericContext[gp.Name] = gp;
        foreach (var gp in method.GenericParameters)
            _genericContext[gp.Name] = gp;

        foreach (var p in method.Parameters)
            _args[p.Name] = p;
    }

    // ── PDB sequence point helper ──────────────────────────────────────────
    private void EmitSequencePoint(Instruction instruction, SourceSpan span)
    {
        if (_debugDoc is null) return;
        var sp = new SequencePoint(instruction, _debugDoc)
        {
            StartLine   = span.Start.Line,
            StartColumn = span.Start.Column + 1, // ANTLR 0-based → PDB 1-based
            EndLine     = span.End.Line,
            EndColumn   = span.End.Column + 1,
        };
        _method.DebugInformation.SequencePoints.Add(sp);
    }

    private TypeReference ResolveType(TypeNode type)
        => ResolveType(_module, type, _imports, _userTypes, _genericContext, _diags, type.Span);

    public void EmitFunctionBody(FunctionDeclNode fn)
    {
        EnterScope();

        // Emit a NOP at function entry for breakpoint support
        if (_debugDoc is not null)
        {
            var nop = _il.Create(OpCodes.Nop);
            _il.Append(nop);
            EmitSequencePoint(nop, fn.Span);

            // Fix the root debug scope start to this first instruction
            if (_scope.DebugScope is not null)
                _scope.DebugScope.Start = new InstructionOffset(nop);
        }

        if (fn.Body is FunctionBlockBodyNode fb)
        {
            EmitBlock(fb.Block);
            EnsureRet(fn.Span, _method.ReturnType);
        }
        else if (fn.Body is FunctionExprBodyNode fe)
        {
            if (_method.ReturnType.MetadataType == MetadataType.Void)
            {
                EmitExpr(fe.Expr, expected: null);
                if (InferExprType(fe.Expr).MetadataType != MetadataType.Void)
                    _il.Append(_il.Create(OpCodes.Pop));
                _il.Append(_il.Create(OpCodes.Ret));
            }
            else
            {
                EmitExpr(fe.Expr, expected: _method.ReturnType);
                CoerceTopIfNeeded(fe.Expr.Span, InferExprType(fe.Expr), _method.ReturnType);
                _il.Append(_il.Create(OpCodes.Ret));
            }
        }
        else
        {
            _diags.Add(new CodeGenDiagnostic(fn.Span, "CG2000", CodeGenSeverity.Error, Msg.Diag("CG2000", fn.Name.Text)));
            _il.Append(_il.Create(OpCodes.Ret));
        }

        // Finalize root debug scope: explicitly point to the last instruction (Ret)
        // for coreclr debugger compatibility
        if (_debugDoc is not null && _method.DebugInformation.Scope is not null
            && _method.Body.Instructions.Count > 0)
        {
            _method.DebugInformation.Scope.Start =
                new InstructionOffset(_method.Body.Instructions.First());
            _method.DebugInformation.Scope.End =
                new InstructionOffset(_method.Body.Instructions.Last());
        }

        ExitScope();
    }

    private void EnsureRet(SourceSpan span, TypeReference retType)
    {
        // Skip trailing NOPs (from debug scope closures) when checking for Ret
        var hasRet = false;
        for (int i = _method.Body.Instructions.Count - 1; i >= 0; i--)
        {
            var op = _method.Body.Instructions[i].OpCode;
            if (op == OpCodes.Ret) { hasRet = true; break; }
            if (op != OpCodes.Nop) break;
        }
        if (_method.Body.Instructions.Count == 0 || !hasRet)
        {
            if (retType.MetadataType != MetadataType.Void)
            {
                _diags.Add(new CodeGenDiagnostic(span, "CG2001", CodeGenSeverity.Error,
                    Msg.Diag("CG2001.error", _method.Name)));
                EmitDefault(retType);
            }
            _il.Append(_il.Create(OpCodes.Ret));
        }
    }

    /* =========================
     *  Scopes
     * ========================= */

    internal void EnterScope()
    {
        _scope = new Scope(_scope);

        if (_debugDoc is not null)
        {
            // Emit a NOP as an anchor for the scope start if no instructions exist yet
            if (_method.Body.Instructions.Count == 0)
            {
                var anchor = _il.Create(OpCodes.Nop);
                _il.Append(anchor);
            }

            var startInstr = _method.Body.Instructions.Last();
            var dbgScope = new ScopeDebugInformation(startInstr, null);
            _scope.DebugScope = dbgScope;

            // Attach: root scope → MethodDebugInformation, child → parent.Scopes
            if (_scope.Parent?.DebugScope is { } parentDbg)
                parentDbg.Scopes.Add(dbgScope);
            else
                _method.DebugInformation.Scope ??= dbgScope;
        }
    }

    internal void ExitScope()
    {
        // Close the debug scope with a NOP anchor so the variable lifetime
        // ends here instead of stretching to the method's last instruction.
        // Only emit when:
        //  1) The scope actually has variables (otherwise no benefit)
        //  2) The last instruction is NOT Ret/Br (to avoid unreachable IL)
        if (_debugDoc is not null && _scope.DebugScope is { } dbgScope
            && dbgScope.Variables.Count > 0
            && _method.Body.Instructions.Count > 0)
        {
            var lastOp = _method.Body.Instructions.Last().OpCode;
            if (lastOp != OpCodes.Ret && lastOp != OpCodes.Br
                && lastOp != OpCodes.Br_S && lastOp != OpCodes.Throw
                && lastOp != OpCodes.Rethrow && lastOp != OpCodes.Leave
                && lastOp != OpCodes.Leave_S)
            {
                var endNop = _il.Create(OpCodes.Nop);
                _il.Append(endNop);
                dbgScope.End = new InstructionOffset(endNop);
            }
        }

        _scope = _scope.Parent ?? _scope;
    }

    private VariableDefinition DeclareLocal(string name, TypeReference type)
    {
        var v = new VariableDefinition(type);
        _method.Body.Variables.Add(v);
        _scope.Locals[name] = v;

        // Record variable name in the current debug scope for PDB
        if (_debugDoc is not null && _scope.DebugScope is { } dbgScope)
        {
            dbgScope.Variables.Add(new VariableDebugInformation(v, name));
        }

        return v;
    }

    private VariableDefinition? LookupLocal(string name)
    {
        for (var s = _scope; s != null; s = s.Parent)
        {
            if (s.Locals.TryGetValue(name, out var v))
                return v;
        }
        return null;
    }

    private ParameterDefinition? LookupArg(string name)
        => _args.TryGetValue(name, out var p) ? p : null;

    /* =========================
     *  Statements
     * ========================= */

    private void EmitBlock(BlockStmtNode block)
    {
        EnterScope();
        foreach (var st in block.Statements)
            EmitStmt(st);
        ExitScope();
    }

    private void EmitStmt(StmtNode st)
    {
        // Emit a sequence point for each non-block statement
        if (_debugDoc is not null && st is not BlockStmtNode)
        {
            var nop = _il.Create(OpCodes.Nop);
            _il.Append(nop);
            EmitSequencePoint(nop, st.Span);
        }

        switch (st)
        {
            case BlockStmtNode b:
                EmitBlock(b);
                break;

            case VarDeclStmtNode v:
                EmitVarDecl(v);
                break;

            case ExprStmtNode e:
                EmitExpr(e.Expr, expected: null);
                if (InferExprType(e.Expr).MetadataType != MetadataType.Void)
                    _il.Append(_il.Create(OpCodes.Pop));
                break;

            case ReturnStmtNode r:
                if (r.Value is null)
                {
                    if (_method.ReturnType.MetadataType != MetadataType.Void)
                    {
                        _diags.Add(new CodeGenDiagnostic(r.Span, "CG2101", CodeGenSeverity.Error,
                            Msg.Diag("CG2101", _method.Name)));
                        EmitDefault(_method.ReturnType);
                    }
                    _il.Append(_il.Create(OpCodes.Ret));
                }
                else
                {
                    EmitExpr(r.Value, expected: _method.ReturnType);
                    var actual = InferExprType(r.Value);
                    CoerceTopIfNeeded(r.Value.Span, actual, _method.ReturnType);
                    _il.Append(_il.Create(OpCodes.Ret));
                }
                break;

            case IfStmtNode iff:
                EmitIf(iff);
                break;

            case WhileStmtNode wh:
                EmitWhile(wh);
                break;

            case TryStmtNode ts:
                EmitTryStmt(ts);
                break;

            case EmptyStmtNode:
                break; // no-op — ASI may generate extra semicolons; silently ignore

            default:
                _diags.Add(new CodeGenDiagnostic(st.Span, "CG2199", CodeGenSeverity.Error,
                    Msg.Diag("CG2199", st.GetType().Name)));
                break;
        }
    }

    private void EmitVarDecl(VarDeclStmtNode v)
    {
        var name = v.Name.Text;

        TypeReference varType;
        if (v.Type is not null)
        {
            varType = ResolveType(_module, v.Type, _imports, _userTypes, _genericContext, _diags, v.Span);
        }
        else if (v.Init is not null)
        {
            varType = InferExprType(v.Init);
        }
        else
        {
            _diags.Add(new CodeGenDiagnostic(v.Span, "CG2201", CodeGenSeverity.Error,
                Msg.Diag("CG2201", name)));
            varType = _module.TypeSystem.Object;
        }

        var local = DeclareLocal(name, varType);

        if (v.Init is not null)
        {
            EmitExpr(v.Init, expected: varType);
            CoerceTopIfNeeded(v.Init.Span, InferExprType(v.Init), varType);
            _il.Append(_il.Create(OpCodes.Stloc, local));
        }
        else
        {
            EmitDefault(varType);
            _il.Append(_il.Create(OpCodes.Stloc, local));
        }
    }

    private void EmitIf(IfStmtNode iff)
    {
        var elseLabel = _il.Create(OpCodes.Nop);
        var endLabel = _il.Create(OpCodes.Nop);

        EmitExpr(iff.Condition, expected: _module.TypeSystem.Boolean);
        CoerceTopIfNeeded(iff.Condition.Span, InferExprType(iff.Condition), _module.TypeSystem.Boolean);

        _il.Append(_il.Create(OpCodes.Brfalse, elseLabel));

        EmitBlock(iff.Then);

        _il.Append(_il.Create(OpCodes.Br, endLabel));
        _il.Append(elseLabel);

        if (iff.Else is not null)
            EmitStmt(iff.Else);

        _il.Append(endLabel);
    }

    private void EmitWhile(WhileStmtNode wh)
    {
        var start = _il.Create(OpCodes.Nop);
        var end = _il.Create(OpCodes.Nop);

        _il.Append(start);

        EmitExpr(wh.Condition, expected: _module.TypeSystem.Boolean);
        CoerceTopIfNeeded(wh.Condition.Span, InferExprType(wh.Condition), _module.TypeSystem.Boolean);
        _il.Append(_il.Create(OpCodes.Brfalse, end));

        EmitBlock(wh.Body);
        _il.Append(_il.Create(OpCodes.Br, start));
        _il.Append(end);
    }

    private void EmitTryStmt(TryStmtNode ts)
    {
        var tryStart = _il.Create(OpCodes.Nop);
        _il.Append(tryStart);

        EmitBlock(ts.TryBlock);

        var endLabel = _il.Create(OpCodes.Nop);
        _il.Append(_il.Create(OpCodes.Leave, endLabel));

        var handlerStarts = new List<Instruction>();

        foreach (var c in ts.Catches)
        {
            var hs = _il.Create(OpCodes.Nop);
            handlerStarts.Add(hs);
            _il.Append(hs);

            var catchType = c.Type is null
                ? _module.ImportReference(typeof(Exception))
                : ResolveType(_module, c.Type, _imports, _userTypes, _genericContext, _diags, c.Span);

            var exName = c.Name?.Text ?? FreshTempName("__aura_ex");
            var exVar = DeclareLocal(exName, catchType);
            _il.Append(_il.Create(OpCodes.Stloc, exVar));

            EnterScope();
            _scope.Locals[exName] = exVar;

            EmitBlock(c.Body);

            ExitScope();

            _il.Append(_il.Create(OpCodes.Leave, endLabel));
        }

        Instruction? finallyStart = null;
        if (ts.Finally is not null)
        {
            finallyStart = _il.Create(OpCodes.Nop);
            handlerStarts.Add(finallyStart);
            _il.Append(finallyStart);

            EmitBlock(ts.Finally);

            _il.Append(_il.Create(OpCodes.Endfinally));
        }

        _il.Append(endLabel);

        var tryEnd = handlerStarts.Count > 0 ? handlerStarts[0] : endLabel;

        for (int i = 0; i < ts.Catches.Count; i++)
        {
            var c = ts.Catches[i];
            var hs = handlerStarts[i];
            var he = (i + 1 < handlerStarts.Count) ? handlerStarts[i + 1] : endLabel;

            var catchType = c.Type is null
                ? _module.ImportReference(typeof(Exception))
                : ResolveType(_module, c.Type, _imports, _userTypes, _genericContext, _diags, c.Span);

            _method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = catchType,
                TryStart = tryStart,
                TryEnd = tryEnd,
                HandlerStart = hs,
                HandlerEnd = he
            });
        }

        if (finallyStart is not null)
        {
            _method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
            {
                TryStart = tryStart,
                TryEnd = tryEnd,
                HandlerStart = finallyStart,
                HandlerEnd = endLabel
            });
        }
    }

    /* =========================
     *  Expressions
     * ========================= */

    private TypeReference InferExprType(ExprNode expr)
    {
        switch (expr)
        {
            case LiteralExprNode lit:
                return lit.Kind switch
                {
                    LiteralKind.Int => _module.TypeSystem.Int32,
                    LiteralKind.Float => _module.TypeSystem.Double,
                    LiteralKind.String => _module.TypeSystem.String,
                    LiteralKind.Char => _module.TypeSystem.Char,
                    LiteralKind.True => _module.TypeSystem.Boolean,
                    LiteralKind.False => _module.TypeSystem.Boolean,
                    LiteralKind.Null => _module.TypeSystem.Object,
                    _ => _module.TypeSystem.Object
                };

            case InterpolatedStringExprNode:
                return _module.TypeSystem.String;

            case NameExprNode n:
                {
                    var local = LookupLocal(n.Name.Text);
                    if (local is not null) return local.VariableType;
                    var arg = LookupArg(n.Name.Text);
                    if (arg is not null) return arg.ParameterType;

                    // captured?
                    if (_closureFields is not null && _closureFields.TryGetValue(n.Name.Text, out var f))
                        return f.FieldType;

                    // implicit this member?
                    if (_isInstanceMethod && TryResolveThisMember(n.Name.Text, out var mt))
                        return mt;

                    return _module.TypeSystem.Object;
                }

            case UnaryExprNode u:
    return u.Op switch
    {
        "!" => _module.TypeSystem.Boolean,
        "await" => InferAwaitResultType(InferExprType(u.Operand), u.Span),
        _ => InferExprType(u.Operand)
    };
case BinaryExprNode b:
                return b.Op switch
                {
                    "==" or "!=" or "<" or "<=" or ">" or ">=" => _module.TypeSystem.Boolean,
                    "&&" or "||" => _module.TypeSystem.Boolean,
                    "??" => InferExprType(b.Left),
                    "+" => (InferExprType(b.Left).FullName == _module.TypeSystem.String.FullName ||
                           InferExprType(b.Right).FullName == _module.TypeSystem.String.FullName)
                           ? _module.TypeSystem.String
                           : InferExprType(b.Left),
                    _ => InferExprType(b.Left)
                };

            case ConditionalExprNode c:
                {
                    var t1 = InferExprType(c.Then);
                    var t2 = InferExprType(c.Else);
                    if (t1.FullName == t2.FullName) return t1;
                    return _module.TypeSystem.Object;
                }

            case AssignmentExprNode a:
                return InferExprType(a.Right);

            case CallExprNode call:
                {
                    var m = TryResolveCallTarget(call, out _, out _, out _);
                    if (m is not null) return m.ReturnType;
                    return _module.TypeSystem.Object;
                }

            case MemberAccessExprNode ma:
                {
                    // First, try user-defined types (Cecil TypeDefinition)
                    var tgtType = InferExprType(ma.Target);
                    TypeDefinition? tgtTd = null;
                    if (tgtType is TypeDefinition tdd2) tgtTd = tdd2;
                    else _userTypes.TryGetValue(tgtType.Name, out tgtTd);

                    if (tgtTd is not null)
                    {
                        // Check inheritance chain
                        var search = tgtTd;
                        while (search is not null)
                        {
                            var getter = search.Methods.FirstOrDefault(m => m.Name == $"get_{ma.Member.Text}");
                            if (getter is not null) return getter.ReturnType;
                            var bf = search.Fields.FirstOrDefault(f => f.Name == $"<{ma.Member.Text}>k__BackingField");
                            if (bf is not null) return bf.FieldType;
                            var next = search.BaseType;
                            if (next is null) break;
                            _userTypes.TryGetValue(next.Name, out search);
                        }
                        return _module.TypeSystem.Object;
                    }

                    var mi = TryResolveMember(ma, out _, out _, out _);
                    if (mi is PropertyInfo pi)
                        return _module.ImportReference(pi.PropertyType);
                    if (mi is FieldInfo fi)
                        return _module.ImportReference(fi.FieldType);
                    if (mi is MethodInfo meth)
                        return _module.ImportReference(meth.ReturnType);
                    return _module.TypeSystem.Object;
                }

            case SeqExprNode se:
                return se.Exprs.Count == 0 ? _module.TypeSystem.Void : InferExprType(se.Exprs[^1]);

            case LetExprNode le:
                return InferExprType(le.Body);

            case TryCatchExprNode te:
                return InferExprType(te.TryExpr);

            case TypeIsExprNode:
                return _module.TypeSystem.Boolean;

            case CastExprNode ce:
                return ResolveType(_module, ce.Type, _imports, _userTypes, _genericContext, _diags, ce.Span);

            case LambdaExprNode lam:
                return InferLambdaDelegateType(lam);

            case NewExprNode ne:
                return ResolveType(_module, ne.TypeRef, _imports, _userTypes, _genericContext, _diags, ne.Span);

            case BuilderNewExprNode:
                return _module.TypeSystem.Object;

            default:
                _diags.Add(new CodeGenDiagnostic(expr.Span, "CG2001", CodeGenSeverity.Warning,
                    Msg.Diag("CG2001.warn", expr.GetType().Name)));
                return _module.TypeSystem.Object;
        }
    }

private TypeReference InferAwaitResultType(TypeReference taskType, SourceSpan span)
{
    // Task<T> => T, Task => void
    if (taskType is GenericInstanceType git &&
        git.ElementType.FullName == "System.Threading.Tasks.Task`1" &&
        git.GenericArguments.Count == 1)
    {
        return git.GenericArguments[0];
    }

    if (taskType.FullName == typeof(Task).FullName)
    {
        return _module.TypeSystem.Void;
    }

    _diags.Add(new CodeGenDiagnostic(span, "CG3010", CodeGenSeverity.Error,
        Msg.Diag("CG3010", taskType.FullName)));
    return _module.TypeSystem.Object;
}



    private bool TryResolveThisMember(string name, out TypeReference type)
    {
        // field?
        var f = _declaringType.Fields.FirstOrDefault(ff => ff.Name == name);
        if (f is not null)
        {
            type = f.FieldType;
            return true;
        }

        // property?
        var p = _declaringType.Properties.FirstOrDefault(pp => pp.Name == name);
        if (p is not null)
        {
            type = p.PropertyType;
            return true;
        }

        // method group not a value
        type = _module.TypeSystem.Object;
        return false;
    }

    private void EmitExpr(ExprNode expr, TypeReference? expected)
    {
        // Emit sequence point for significant expressions (calls, assignments, member access, try/catch)
        // Skip trivial sub-expressions (literals, names, binary operands) to avoid noise
        if (_debugDoc is not null && expr is CallExprNode or AssignmentExprNode
            or MemberAccessExprNode or TryCatchExprNode or LambdaExprNode or LetExprNode)
        {
            var nop = _il.Create(OpCodes.Nop);
            _il.Append(nop);
            EmitSequencePoint(nop, expr.Span);
        }

        switch (expr)
        {
            case LiteralExprNode lit:
                EmitLiteral(lit);
                break;

            case InterpolatedStringExprNode istr:
                EmitInterpolatedString(istr);
                break;

            case NameExprNode n:
                EmitName(n);
                break;

            case UnaryExprNode u:
                EmitUnary(u);
                break;

            case BinaryExprNode b:
                EmitBinary(b);
                break;

            case ConditionalExprNode c:
                EmitConditional(c, expected);
                break;

            case AssignmentExprNode a:
                EmitAssignment(a);
                break;

            case MemberAccessExprNode ma:
                EmitMemberAccessValue(ma);
                break;

            case CallExprNode call:
                EmitCall(call);
                break;

            case SeqExprNode se:
                EmitSeqExpr(se, expected ?? _module.TypeSystem.Object);
                break;

            case LetExprNode le:
                EmitLetExpr(le, expected);
                break;

            case TryCatchExprNode te:
                EmitTryCatchExpr(te, expected);
                break;

            case TypeIsExprNode ti:
                EmitTypeIs(ti);
                break;

            case CastExprNode ce:
                EmitCast(ce);
                break;

            case LambdaExprNode lam:
                EmitLambda(lam, expected);
                break;

            case NewExprNode newExpr:
                EmitNewExpr(newExpr);
                break;

            case BuilderNewExprNode builderNew:
                EmitBuilderNewExpr(builderNew);
                break;

            default:
                _diags.Add(new CodeGenDiagnostic(expr.Span, "CG3000", CodeGenSeverity.Error,
                    Msg.Diag("CG3000", expr.GetType().Name)));
                _il.Append(_il.Create(OpCodes.Ldnull));
                break;
        }
    }

    private void EmitLiteral(LiteralExprNode lit)
    {
        switch (lit.Kind)
        {
            case LiteralKind.Int:
                if (int.TryParse(lit.RawText, out var i))
                    _il.Append(_il.Create(OpCodes.Ldc_I4, i));
                else
                {
                    _diags.Add(new CodeGenDiagnostic(lit.Span, "CG3001", CodeGenSeverity.Error, Msg.Diag("CG3001", lit.RawText)));
                    _il.Append(_il.Create(OpCodes.Ldc_I4_0));
                }
                break;

            case LiteralKind.String:
                var s = lit.RawText;
                if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                    s = s.Substring(1, s.Length - 2);
                _il.Append(_il.Create(OpCodes.Ldstr, s));
                break;

            case LiteralKind.True:
                _il.Append(_il.Create(OpCodes.Ldc_I4_1));
                break;

            case LiteralKind.False:
                _il.Append(_il.Create(OpCodes.Ldc_I4_0));
                break;

            case LiteralKind.Float:
                if (double.TryParse(lit.RawText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                    _il.Append(_il.Create(OpCodes.Ldc_R8, d));
                else
                {
                    _diags.Add(new CodeGenDiagnostic(lit.Span, "CG3002", CodeGenSeverity.Error, Msg.Diag("CG3002.float", lit.RawText)));
                    _il.Append(_il.Create(OpCodes.Ldc_R8, 0.0));
                }
                break;

            case LiteralKind.Char:
                var raw = lit.RawText;
                if (raw.Length >= 3 && raw[0] == '\'' && raw[^1] == '\'')
                    _il.Append(_il.Create(OpCodes.Ldc_I4, (int)raw[1]));
                else if (raw.Length == 1)
                    _il.Append(_il.Create(OpCodes.Ldc_I4, (int)raw[0]));
                else
                {
                    _diags.Add(new CodeGenDiagnostic(lit.Span, "CG3003", CodeGenSeverity.Error, Msg.Diag("CG3003.char", lit.RawText)));
                    _il.Append(_il.Create(OpCodes.Ldc_I4_0));
                }
                break;

            case LiteralKind.Null:
                _il.Append(_il.Create(OpCodes.Ldnull));
                break;

            default:
                _diags.Add(new CodeGenDiagnostic(lit.Span, "CG3004", CodeGenSeverity.Warning, Msg.Diag("CG3004.literal", lit.Kind)));
                _il.Append(_il.Create(OpCodes.Ldnull));
                break;
        }
    }

    private void EmitName(NameExprNode n)
    {
        var name = n.Name.Text;

        var local = LookupLocal(name);
        if (local is not null)
        {
            _il.Append(_il.Create(OpCodes.Ldloc, local));
            return;
        }

        var arg = LookupArg(name);
        if (arg is not null)
        {
            EmitLdarg(arg);
            return;
        }

        // closure captured variable as field on "this" (display class method)
        if (_closureFields is not null && _closureFields.TryGetValue(name, out var field))
        {
            if (!_isInstanceMethod)
            {
                _diags.Add(new CodeGenDiagnostic(n.Span, "CG3505", CodeGenSeverity.Error,
                    Msg.Diag("CG3505", name)));
                _il.Append(_il.Create(OpCodes.Ldnull));
                return;
            }

            _il.Append(_il.Create(OpCodes.Ldarg_0));
            _il.Append(_il.Create(OpCodes.Ldfld, field));
            return;
        }

        // v3: implicit this member access (walks up inheritance chain)
        if (_isInstanceMethod)
        {
            var searchTd = _declaringType;
            while (searchTd is not null)
            {
                // field?
                var f = searchTd.Fields.FirstOrDefault(ff => ff.Name == name);
                if (f is not null)
                {
                    _il.Append(_il.Create(OpCodes.Ldarg_0));
                    _il.Append(_il.Create(OpCodes.Ldfld, f));
                    return;
                }

                // property?
                var p = searchTd.Properties.FirstOrDefault(pp => pp.Name == name);
                if (p?.GetMethod is not null)
                {
                    _il.Append(_il.Create(OpCodes.Ldarg_0));
                    _il.Append(_il.Create(OpCodes.Callvirt, _module.ImportReference(p.GetMethod)));
                    return;
                }

                // backing field?
                var bf = searchTd.Fields.FirstOrDefault(ff => ff.Name == $"<{name}>k__BackingField");
                if (bf is not null)
                {
                    _il.Append(_il.Create(OpCodes.Ldarg_0));
                    _il.Append(_il.Create(OpCodes.Ldfld, bf));
                    return;
                }

                // walk to base class
                var baseRef = searchTd.BaseType;
                if (baseRef is null) break;
                _userTypes.TryGetValue(baseRef.Name, out searchTd);
            }
        }

        _diags.Add(new CodeGenDiagnostic(n.Span, "CG3002", CodeGenSeverity.Error, Msg.Diag("CG3002.name", name)));
        _il.Append(_il.Create(OpCodes.Ldnull));
    }

    private void EmitLdarg(ParameterDefinition arg)
    {
        var idx = _method.Parameters.IndexOf(arg);
        // Instance method has implicit arg0 'this' in IL, but Cecil Parameters excludes it.
        // ldarg indices count including 'this' when method has this.
        var ilIndex = _method.HasThis ? idx + 1 : idx;

        if (ilIndex == 0) _il.Append(_il.Create(OpCodes.Ldarg_0));
        else if (ilIndex == 1) _il.Append(_il.Create(OpCodes.Ldarg_1));
        else if (ilIndex == 2) _il.Append(_il.Create(OpCodes.Ldarg_2));
        else if (ilIndex == 3) _il.Append(_il.Create(OpCodes.Ldarg_3));
        else if (ilIndex <= byte.MaxValue) _il.Append(_il.Create(OpCodes.Ldarg, (byte)ilIndex));
        else _il.Append(_il.Create(OpCodes.Ldarg, ilIndex));
    }

    private void EmitUnary(UnaryExprNode u)
    {
        EmitExpr(u.Operand, expected: null);

        switch (u.Op)
        {
            case "-":
                _il.Append(_il.Create(OpCodes.Neg));
                break;

            case "!":
                _il.Append(_il.Create(OpCodes.Ldc_I4_0));
                _il.Append(_il.Create(OpCodes.Ceq));
                break;

            case "await":
                EmitAwaitUnary(u);
                break;

            default:
                _diags.Add(new CodeGenDiagnostic(u.Span, "CG3003", CodeGenSeverity.Error, Msg.Diag("CG3003.unary", u.Op)));
                break;
        }
    }

private void EmitAwaitUnary(UnaryExprNode u)
{
    // Operand already emitted onto stack.
    var taskType = InferExprType(u.Operand);

    var clrTaskType = ResolveClrTypeFromTypeReference(taskType);
    if (clrTaskType is null)
    {
        _diags.Add(new CodeGenDiagnostic(u.Span, "CG3011", CodeGenSeverity.Error,
            Msg.Diag("CG3011", taskType.FullName)));
        _il.Append(_il.Create(OpCodes.Pop));
        return;
    }

    var getAwaiter = clrTaskType.GetMethod("GetAwaiter", Type.EmptyTypes);
    if (getAwaiter is null)
    {
        _diags.Add(new CodeGenDiagnostic(u.Span, "CG3012", CodeGenSeverity.Error,
            Msg.Diag("CG3012", clrTaskType.FullName ?? clrTaskType.Name)));
        _il.Append(_il.Create(OpCodes.Pop));
        return;
    }

    var getAwaiterRef = _module.ImportReference(getAwaiter);
    _il.Append(_il.Create(OpCodes.Callvirt, getAwaiterRef));

    var awaiterType = getAwaiterRef.ReturnType;
    var awaiterLocal = new VariableDefinition(awaiterType);
    _method.Body.Variables.Add(awaiterLocal);
    _il.Append(_il.Create(OpCodes.Stloc, awaiterLocal));

    var clrAwaiterType = ResolveClrTypeFromTypeReference(awaiterType);
    if (clrAwaiterType is null)
    {
        _diags.Add(new CodeGenDiagnostic(u.Span, "CG3013", CodeGenSeverity.Error,
            Msg.Diag("CG3013", awaiterType.FullName)));
        return;
    }

    var getResult = clrAwaiterType.GetMethod("GetResult", Type.EmptyTypes);
    if (getResult is null)
    {
        _diags.Add(new CodeGenDiagnostic(u.Span, "CG3014", CodeGenSeverity.Error,
            Msg.Diag("CG3014", clrAwaiterType.FullName ?? clrAwaiterType.Name)));
        return;
    }

    var getResultRef = _module.ImportReference(getResult);

    if (awaiterType.IsValueType)
    {
        _il.Append(_il.Create(OpCodes.Ldloca, awaiterLocal));
        _il.Append(_il.Create(OpCodes.Call, getResultRef));
    }
    else
    {
        _il.Append(_il.Create(OpCodes.Ldloc, awaiterLocal));
        _il.Append(_il.Create(OpCodes.Callvirt, getResultRef));
    }
}



    private void EmitBinary(BinaryExprNode b)
    {
        if (b.Op == "&&")
        {
            var falseLabel = _il.Create(OpCodes.Nop);
            var endLabel = _il.Create(OpCodes.Nop);

            EmitExpr(b.Left, expected: _module.TypeSystem.Boolean);
            _il.Append(_il.Create(OpCodes.Brfalse, falseLabel));

            EmitExpr(b.Right, expected: _module.TypeSystem.Boolean);
            _il.Append(_il.Create(OpCodes.Br, endLabel));

            _il.Append(falseLabel);
            _il.Append(_il.Create(OpCodes.Ldc_I4_0));
            _il.Append(endLabel);
            return;
        }

        if (b.Op == "||")
        {
            var trueLabel = _il.Create(OpCodes.Nop);
            var endLabel = _il.Create(OpCodes.Nop);

            EmitExpr(b.Left, expected: _module.TypeSystem.Boolean);
            _il.Append(_il.Create(OpCodes.Brtrue, trueLabel));

            EmitExpr(b.Right, expected: _module.TypeSystem.Boolean);
            _il.Append(_il.Create(OpCodes.Br, endLabel));

            _il.Append(trueLabel);
            _il.Append(_il.Create(OpCodes.Ldc_I4_1));
            _il.Append(endLabel);
            return;
        }

        if (b.Op == "??")
        {
            var end = _il.Create(OpCodes.Nop);

            EmitExpr(b.Left, expected: null);
            _il.Append(_il.Create(OpCodes.Dup));
            _il.Append(_il.Create(OpCodes.Brtrue, end));
            _il.Append(_il.Create(OpCodes.Pop));
            EmitExpr(b.Right, expected: null);
            _il.Append(end);
            return;
        }

        EmitExpr(b.Left, expected: null);
        EmitExpr(b.Right, expected: null);

        switch (b.Op)
        {
            case "+":
                var lt = InferExprType(b.Left);
                var rt = InferExprType(b.Right);
                if (lt.FullName == _module.TypeSystem.String.FullName || rt.FullName == _module.TypeSystem.String.FullName)
                {
                    if (lt.IsValueType) _il.Append(_il.Create(OpCodes.Box, lt));
                    if (rt.IsValueType) _il.Append(_il.Create(OpCodes.Box, rt));
                    var concat = _module.ImportReference(typeof(string).GetMethod("Concat", new[] { typeof(object), typeof(object) })!);
                    _il.Append(_il.Create(OpCodes.Call, concat));
                }
                else
                {
                    _il.Append(_il.Create(OpCodes.Add));
                }
                break;

            case "-": _il.Append(_il.Create(OpCodes.Sub)); break;
            case "*": _il.Append(_il.Create(OpCodes.Mul)); break;
            case "/": _il.Append(_il.Create(OpCodes.Div)); break;
            case "%": _il.Append(_il.Create(OpCodes.Rem)); break;

            case "==":
            {
                var lt2 = InferExprType(b.Left);
                var rt2 = InferExprType(b.Right);
                if (lt2.FullName == _module.TypeSystem.String.FullName ||
                    rt2.FullName == _module.TypeSystem.String.FullName)
                {
                    var strEquals = _module.ImportReference(
                        typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) })!);
                    _il.Append(_il.Create(OpCodes.Call, strEquals));
                }
                else
                {
                    _il.Append(_il.Create(OpCodes.Ceq));
                }
                break;
            }

            case "!=":
            {
                var lt3 = InferExprType(b.Left);
                var rt3 = InferExprType(b.Right);
                if (lt3.FullName == _module.TypeSystem.String.FullName ||
                    rt3.FullName == _module.TypeSystem.String.FullName)
                {
                    var strInequality = _module.ImportReference(
                        typeof(string).GetMethod("op_Inequality", new[] { typeof(string), typeof(string) })!);
                    _il.Append(_il.Create(OpCodes.Call, strInequality));
                }
                else
                {
                    _il.Append(_il.Create(OpCodes.Ceq));
                    _il.Append(_il.Create(OpCodes.Ldc_I4_0));
                    _il.Append(_il.Create(OpCodes.Ceq));
                }
                break;
            }

            case "<": _il.Append(_il.Create(OpCodes.Clt)); break;
            case ">": _il.Append(_il.Create(OpCodes.Cgt)); break;

            case "<=":
                _il.Append(_il.Create(OpCodes.Cgt));
                _il.Append(_il.Create(OpCodes.Ldc_I4_0));
                _il.Append(_il.Create(OpCodes.Ceq));
                break;

            case ">=":
                _il.Append(_il.Create(OpCodes.Clt));
                _il.Append(_il.Create(OpCodes.Ldc_I4_0));
                _il.Append(_il.Create(OpCodes.Ceq));
                break;

            default:
                if (b.Op is "&" or "|" or "^" or "<<" or ">>")
                    _diags.Add(new CodeGenDiagnostic(b.Span, "CG4400", CodeGenSeverity.Error, Msg.Diag("CG4400", b.Op)));
                else
                    _diags.Add(new CodeGenDiagnostic(b.Span, "CG3004", CodeGenSeverity.Error, Msg.Diag("CG3004.binary", b.Op)));
                break;
        }
    }

    private void EmitConditional(ConditionalExprNode c, TypeReference? expected)
    {
        var resultType = expected ?? InferExprType(c.Then);
        var tmp = new VariableDefinition(resultType);
        _method.Body.Variables.Add(tmp);

        var elseLabel = _il.Create(OpCodes.Nop);
        var endLabel = _il.Create(OpCodes.Nop);

        EmitExpr(c.Condition, expected: _module.TypeSystem.Boolean);
        CoerceTopIfNeeded(c.Condition.Span, InferExprType(c.Condition), _module.TypeSystem.Boolean);
        _il.Append(_il.Create(OpCodes.Brfalse, elseLabel));

        EmitExpr(c.Then, expected: resultType);
        CoerceTopIfNeeded(c.Then.Span, InferExprType(c.Then), resultType);
        _il.Append(_il.Create(OpCodes.Stloc, tmp));
        _il.Append(_il.Create(OpCodes.Br, endLabel));

        _il.Append(elseLabel);
        EmitExpr(c.Else, expected: resultType);
        CoerceTopIfNeeded(c.Else.Span, InferExprType(c.Else), resultType);
        _il.Append(_il.Create(OpCodes.Stloc, tmp));

        _il.Append(endLabel);
        _il.Append(_il.Create(OpCodes.Ldloc, tmp));
    }

    private void EmitAssignment(AssignmentExprNode a)
    {
        if (a.Op == "??=")
        {
            EmitNullCoalesceAssign(a);
            return;
        }

        if (a.Left is NameExprNode ne)
        {
            var name = ne.Name.Text;

            var local = LookupLocal(name);
            if (local is not null)
            {
                EmitExpr(a.Right, expected: local.VariableType);
                CoerceTopIfNeeded(a.Right.Span, InferExprType(a.Right), local.VariableType);
                _il.Append(_il.Create(OpCodes.Dup));
                _il.Append(_il.Create(OpCodes.Stloc, local));
                return;
            }

            var arg = LookupArg(name);
            if (arg is not null)
            {
                EmitExpr(a.Right, expected: arg.ParameterType);
                CoerceTopIfNeeded(a.Right.Span, InferExprType(a.Right), arg.ParameterType);
                _il.Append(_il.Create(OpCodes.Dup));
                _il.Append(_il.Create(OpCodes.Starg, arg));
                return;
            }

            // captured field assignment (display class)
            if (_closureFields is not null && _closureFields.TryGetValue(name, out var capField))
            {
                EmitThisFieldAssign(capField, a.Right, a.Span);
                return;
            }

            // v3: implicit this field/property assignment
            if (_isInstanceMethod)
            {
                var f = _declaringType.Fields.FirstOrDefault(ff => ff.Name == name);
                if (f is not null)
                {
                    EmitThisFieldAssign(f, a.Right, a.Span);
                    return;
                }

                var p = _declaringType.Properties.FirstOrDefault(pp => pp.Name == name);
                if (p?.SetMethod is not null)
                {
                    EmitThisPropertyAssign(p, a.Right, a.Span);
                    return;
                }
            }
        }

        _diags.Add(new CodeGenDiagnostic(a.Span, "CG3100", CodeGenSeverity.Error,
            Msg.Diag("CG3100.assign", a.Left.GetType().Name)));
        EmitExpr(a.Right, expected: null);
    }

    private void EmitThisFieldAssign(FieldReference field, ExprNode rhs, SourceSpan span)
    {
        if (!_isInstanceMethod)
        {
            _diags.Add(new CodeGenDiagnostic(span, "CG3506", CodeGenSeverity.Error,
                Msg.Diag("CG3506", field.Name)));
            EmitExpr(rhs, expected: null);
            return;
        }

        var tmp = new VariableDefinition(field.FieldType);
        _method.Body.Variables.Add(tmp);

        EmitExpr(rhs, expected: field.FieldType);
        CoerceTopIfNeeded(rhs.Span, InferExprType(rhs), field.FieldType);
        _il.Append(_il.Create(OpCodes.Stloc, tmp));

        _il.Append(_il.Create(OpCodes.Ldarg_0));
        _il.Append(_il.Create(OpCodes.Ldloc, tmp));
        _il.Append(_il.Create(OpCodes.Stfld, field));

        _il.Append(_il.Create(OpCodes.Ldloc, tmp));
    }

    private void EmitThisPropertyAssign(PropertyDefinition prop, ExprNode rhs, SourceSpan span)
    {
        if (!_isInstanceMethod)
        {
            _diags.Add(new CodeGenDiagnostic(span, "CG3507", CodeGenSeverity.Error,
                Msg.Diag("CG3507", prop.Name)));
            EmitExpr(rhs, expected: null);
            return;
        }

        var set = prop.SetMethod;
        if (set is null)
        {
            _diags.Add(new CodeGenDiagnostic(span, "CG3508", CodeGenSeverity.Error,
                Msg.Diag("CG3508", prop.Name)));
            EmitExpr(rhs, expected: null);
            return;
        }

        var tmp = new VariableDefinition(prop.PropertyType);
        _method.Body.Variables.Add(tmp);

        EmitExpr(rhs, expected: prop.PropertyType);
        CoerceTopIfNeeded(rhs.Span, InferExprType(rhs), prop.PropertyType);
        _il.Append(_il.Create(OpCodes.Stloc, tmp));

        _il.Append(_il.Create(OpCodes.Ldarg_0));
        _il.Append(_il.Create(OpCodes.Ldloc, tmp));
        _il.Append(_il.Create(OpCodes.Callvirt, set));

        _il.Append(_il.Create(OpCodes.Ldloc, tmp));
    }

    private void EmitNullCoalesceAssign(AssignmentExprNode a)
    {
        if (a.Left is not NameExprNode ne)
        {
            _diags.Add(new CodeGenDiagnostic(a.Span, "CG3101", CodeGenSeverity.Error, Msg.Diag("CG3101.coalesce")));
            EmitExpr(a.Right, expected: null);
            return;
        }

        var local = LookupLocal(ne.Name.Text);
        if (local is null)
        {
            _diags.Add(new CodeGenDiagnostic(a.Span, "CG3102", CodeGenSeverity.Error, Msg.Diag("CG3102")));
            EmitExpr(a.Right, expected: null);
            return;
        }

        if (local.VariableType.IsValueType)
        {
            _diags.Add(new CodeGenDiagnostic(a.Span, "CG3103", CodeGenSeverity.Error, Msg.Diag("CG3103")));
        }

        var end = _il.Create(OpCodes.Nop);

        _il.Append(_il.Create(OpCodes.Ldloc, local));
        _il.Append(_il.Create(OpCodes.Dup));
        _il.Append(_il.Create(OpCodes.Brtrue, end));
        _il.Append(_il.Create(OpCodes.Pop));

        EmitExpr(a.Right, expected: local.VariableType);
        CoerceTopIfNeeded(a.Right.Span, InferExprType(a.Right), local.VariableType);
        _il.Append(_il.Create(OpCodes.Dup));
        _il.Append(_il.Create(OpCodes.Stloc, local));

        _il.Append(end);
    }

    private void EmitInterpolatedString(InterpolatedStringExprNode s)
    {
        var concatSS = _module.ImportReference(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })!);
        var concatSO = _module.ImportReference(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(object) })!);

        _il.Append(_il.Create(OpCodes.Ldstr, ""));

        foreach (var part in s.Parts)
        {
            switch (part)
            {
                case InterpTextPartNode t:
                    _il.Append(_il.Create(OpCodes.Ldstr, t.Text));
                    _il.Append(_il.Create(OpCodes.Call, concatSS));
                    break;

                case InterpExprPartNode e:
                    EmitExpr(e.Expr, expected: null);
                    var et = InferExprType(e.Expr);
                    if (et.IsValueType) _il.Append(_il.Create(OpCodes.Box, et));
                    _il.Append(_il.Create(OpCodes.Call, concatSO));
                    break;

                default:
                    _il.Append(_il.Create(OpCodes.Ldstr, ""));
                    _il.Append(_il.Create(OpCodes.Call, concatSS));
                    break;
            }
        }
    }

    private void EmitMemberAccessValue(MemberAccessExprNode ma)
    {
        // Check if the target resolves to a user-defined TypeDefinition (Aura class/struct)
        var targetTypeRef = InferExprType(ma.Target);
        if (TryEmitUserTypeMemberAccess(ma, targetTypeRef))
            return;

        // Also handle implicit this member access (e.g. other.x inside a method)
        if (_isInstanceMethod && _declaringType is not null)
        {
            if (TryEmitUserTypeMemberAccessOnExpr(ma, _declaringType))
                return;
        }

        // If the target is implicit this? (not represented here; handled in EmitName)
        var resolved = TryResolveMember(ma, out var isStatic, out var targetType, out _);
        if (resolved is null)
        {
            _diags.Add(new CodeGenDiagnostic(ma.Span, "CG3200", CodeGenSeverity.Error, Msg.Diag("CG3200", ma.Member.Text)));
            _il.Append(_il.Create(OpCodes.Ldnull));
            return;
        }

        if (!isStatic)
        {
            EmitExpr(ma.Target, expected: null);
            var t = InferExprType(ma.Target);
            if (targetType is not null)
                CoerceTopIfNeeded(ma.Target.Span, t, _module.ImportReference(targetType));
        }

        switch (resolved)
        {
            case PropertyInfo pi:
                var getter = pi.GetMethod;
                if (getter is null)
                {
                    _diags.Add(new CodeGenDiagnostic(ma.Span, "CG3201", CodeGenSeverity.Error, Msg.Diag("CG3201", pi.Name)));
                    _il.Append(_il.Create(OpCodes.Ldnull));
                    return;
                }
                var mr = _module.ImportReference(getter);
                _il.Append(_il.Create(isStatic ? OpCodes.Call : OpCodes.Callvirt, mr));
                break;

            case FieldInfo fi:
                var fr = _module.ImportReference(fi);
                _il.Append(_il.Create(isStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, fr));
                break;

            case MethodInfo mi:
                _diags.Add(new CodeGenDiagnostic(ma.Span, "CG3202", CodeGenSeverity.Error,
                    Msg.Diag("CG3202", mi.Name)));
                _il.Append(_il.Create(OpCodes.Ldnull));
                break;
        }
    }

    /// <summary>
    /// Attempts to emit a property/field getter for user-defined (Cecil) types.
    /// Returns true if the member was found and emitted.
    /// </summary>
    private bool TryEmitUserTypeMemberAccess(MemberAccessExprNode ma, TypeReference targetTypeRef)
    {
        TypeDefinition? td = null;
        if (targetTypeRef is TypeDefinition tdd)
            td = tdd;
        else if (targetTypeRef is TypeReference tr)
            _userTypes.TryGetValue(tr.Name, out td);

        if (td is null) return false;

        return TryEmitUserTypeMemberAccessOnExpr(ma, td);
    }

    private bool TryEmitUserTypeMemberAccessOnExpr(MemberAccessExprNode ma, TypeDefinition td)
    {
        var memberName = ma.Member.Text;

        // Look for getter method (auto-property)
        var getter = td.Methods.FirstOrDefault(m => m.Name == $"get_{memberName}");
        if (getter is not null)
        {
            EmitExpr(ma.Target, expected: null);
            _il.Append(_il.Create(OpCodes.Callvirt, _module.ImportReference(getter)));
            return true;
        }

        // Look for backing field directly
        var backingField = td.Fields.FirstOrDefault(f => f.Name == $"<{memberName}>k__BackingField");
        if (backingField is not null)
        {
            EmitExpr(ma.Target, expected: null);
            _il.Append(_il.Create(OpCodes.Ldfld, _module.ImportReference(backingField)));
            return true;
        }

        // Try base class (inheritance)
        if (td.BaseType is TypeReference baseRef)
        {
            TypeDefinition? baseTd = null;
            _userTypes.TryGetValue(baseRef.Name, out baseTd);
            if (baseTd is not null)
            {
                return TryEmitUserTypeMemberAccessOnExpr(
                    new MemberAccessExprNode(ma.Span, ma.Target, ma.Member, ma.TypeArgs), baseTd);
            }
        }

        return false;
    }

    private void EmitNewExpr(NewExprNode n)
    {
        var typeRef = ResolveType(_module, n.TypeRef, _imports, _userTypes, _genericContext, _diags, n.Span);

        // User-defined class/struct?
        if (typeRef is TypeDefinition td || (typeRef is TypeReference tr && _module.Types.Contains(typeRef as TypeDefinition)))
        {
            // Find the TypeDefinition in userTypes
            var typeName = n.TypeRef switch
            {
                NamedTypeNode nt => nt.Name.ToString(),
                _ => null
            };
            TypeDefinition? userTd = null;
            if (typeName is not null)
                _userTypes.TryGetValue(typeName, out userTd);

            if (userTd is null && typeRef is TypeDefinition tdd)
                userTd = tdd;

            if (userTd is not null)
            {
                // Find the default .ctor
                var ctor = userTd.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);
                if (ctor is null)
                {
                    _diags.Add(new CodeGenDiagnostic(n.Span, "CG3100", CodeGenSeverity.Error,
                        Msg.Diag("CG3100.ctor", userTd.Name)));
                    _il.Append(_il.Create(OpCodes.Ldnull));
                    return;
                }

                // newobj → push new instance
                _il.Append(_il.Create(OpCodes.Newobj, _module.ImportReference(ctor)));

                // For each named arg, call the property setter
                foreach (var arg in n.Args)
                {
                    string? propName = null;
                    ExprNode? valueExpr = null;

                    if (arg is NamedArgNode na)
                    {
                        propName = na.Name.Text;
                        valueExpr = na.Value;
                    }
                    else if (arg is PositionalArgNode pa)
                    {
                        // positional args on user types not supported in this path
                        valueExpr = pa.Value;
                    }

                    if (propName is null || valueExpr is null) continue;

                    // Walk inheritance chain to find setter
                    MethodDefinition? setter = null;
                    var searchForSetter = userTd;
                    while (searchForSetter is not null && setter is null)
                    {
                        setter = searchForSetter.Methods.FirstOrDefault(m => m.Name == $"set_{propName}");
                        if (setter is null)
                        {
                            var baseRef2 = searchForSetter.BaseType;
                            searchForSetter = null;
                            if (baseRef2 is not null) _userTypes.TryGetValue(baseRef2.Name, out searchForSetter);
                        }
                    }
                    if (setter is null) continue;

                    // dup the instance reference, push value, call setter
                    _il.Append(_il.Create(OpCodes.Dup));
                    EmitExpr(valueExpr, expected: setter.Parameters.Count > 0 ? setter.Parameters[0].ParameterType : null);
                    _il.Append(_il.Create(OpCodes.Callvirt, _module.ImportReference(setter)));
                }
                return;
            }
        }

        // Fallback: CLR type — find a matching constructor
        var clrType = n.TypeRef switch
        {
            NamedTypeNode nt => TryResolveClrType(nt.Name.ToString(), _imports),
            _ => null
        };
        if (clrType is not null)
        {
            var ctorArgs = n.Args.Select(a => a switch
            {
                PositionalArgNode pa => InferExprType(pa.Value),
                NamedArgNode na => InferExprType(na.Value),
                _ => _module.TypeSystem.Object
            }).ToArray();

            var ctor = clrType.GetConstructor(ctorArgs.Select(t =>
                Type.GetType(t.FullName) ?? typeof(object)).ToArray());
            if (ctor is null)
                ctor = clrType.GetConstructors().FirstOrDefault();

            if (ctor is not null)
            {
                foreach (var arg in n.Args)
                {
                    ExprNode? ve = arg switch
                    {
                        PositionalArgNode pa => pa.Value,
                        NamedArgNode na => na.Value,
                        _ => null
                    };
                    if (ve is not null) EmitExpr(ve, expected: null);
                }
                _il.Append(_il.Create(OpCodes.Newobj, _module.ImportReference(ctor)));
                return;
            }
        }

        _diags.Add(new CodeGenDiagnostic(n.Span, "CG3101", CodeGenSeverity.Error,
            Msg.Diag("CG3101.newexpr", n.TypeRef)));
        _il.Append(_il.Create(OpCodes.Ldnull));
    }

    /// <summary>
    /// Emits builder-based new: <c>new(builder)</c>
    /// Calls builder.GetConstructorDictionary() then builder.Build(dict).
    /// </summary>
    private void EmitBuilderNewExpr(BuilderNewExprNode n)
    {
        // Emit the builder expression onto the stack
        EmitExpr(n.Builder, expected: null);

        // dup for the two calls: GetConstructorDictionary() then Build(dict)
        _il.Append(_il.Create(OpCodes.Dup));

        // Call GetConstructorDictionary() → returns Dictionary<string, object>
        // We need to find the method on the IBuilder interface in userTypes
        if (_userTypes.TryGetValue("IBuilder", out var ibuilderTd))
        {
            var getDictMethod = ibuilderTd.Methods.FirstOrDefault(m => m.Name == "GetConstructorDictionary");
            var buildMethod = ibuilderTd.Methods.FirstOrDefault(m => m.Name == "Build");

            if (getDictMethod is not null && buildMethod is not null)
            {
                _il.Append(_il.Create(OpCodes.Callvirt, _module.ImportReference(getDictMethod)));
                // Stack: [builder, dict]
                _il.Append(_il.Create(OpCodes.Callvirt, _module.ImportReference(buildMethod)));
                // Stack: [result (object/T)]
                return;
            }
        }

        // Fallback: just pop and push null
        _il.Append(_il.Create(OpCodes.Pop));
        _diags.Add(new CodeGenDiagnostic(n.Span, "CG3110", CodeGenSeverity.Error,
            Msg.Diag("CG3110")));
        _il.Append(_il.Create(OpCodes.Ldnull));
    }

    private void EmitCall(CallExprNode call)
    {
        // Handle derivateof marker: __aura_derivateof_{fnName}()
        // Resolves to loading the synthesized __aura_op_{fnName}_* properties from the declaring type
        if (call.Callee is NameExprNode derivMarker && derivMarker.Name.Text.StartsWith("__aura_derivateof_"))
        {
            EmitDeriveofMarker(derivMarker.Name.Text, call.Span);
            return;
        }

        var mr = TryResolveCallTarget(call, out var callKind, out var instanceExpr, out var implicitThis);

        if (mr is null)
        {
            EmitDelegateInvoke(call);
            return;
        }

        if (callKind == CallKind.Instance)
        {
            if (implicitThis)
            {
                _il.Append(_il.Create(OpCodes.Ldarg_0));
            }
            else if (instanceExpr is not null)
            {
                EmitExpr(instanceExpr, expected: null);
                var t = InferExprType(instanceExpr);
                CoerceTopIfNeeded(instanceExpr.Span, t, mr.DeclaringType);
            }
            else
            {
                _il.Append(_il.Create(OpCodes.Ldarg_0));
            }
        }

        var ps = mr.Parameters;
        for (int i = 0; i < call.Args.Count; i++)
        {
            var arg = call.Args[i];
            if (arg is PlaceholderArgNode)
            {
                _diags.Add(new CodeGenDiagnostic(arg.Span, "CG3301", CodeGenSeverity.Error,
                    Msg.Diag("CG3301")));
                _il.Append(_il.Create(OpCodes.Ldnull));
                continue;
            }

            ExprNode? exprArg = arg switch
            {
                PositionalArgNode pa => pa.Value,
                NamedArgNode na => na.Value,
                _ => null
            };

            if (exprArg is null)
            {
                _il.Append(_il.Create(OpCodes.Ldnull));
                continue;
            }

            var expectedParamType = i < ps.Count ? ps[i].ParameterType : null;
            EmitExpr(exprArg, expected: expectedParamType);
            if (expectedParamType is not null)
                CoerceTopIfNeeded(exprArg.Span, InferExprType(exprArg), expectedParamType);
        }

        _il.Append(_il.Create(callKind == CallKind.Static ? OpCodes.Call : OpCodes.Callvirt, mr));
    }

    /// <summary>
    /// Resolves __aura_derivateof_{fnName} markers by finding all __aura_op_{fnName}_* properties
    /// on the declaring type. Generates a display class with named fields for each op delegate,
    /// allowing type-safe access by op name (e.g., derivateof(process).validate).
    /// </summary>
    private void EmitDeriveofMarker(string markerName, SourceSpan span)
    {
        var fnName = markerName["__aura_derivateof_".Length..];
        var prefix = $"__aura_op_{fnName}_";

        // Find all op properties matching the prefix on the declaring type
        var opProps = _declaringType?.Properties
            .Where(p => p.Name.StartsWith(prefix))
            .ToList();

        if (opProps is null || opProps.Count == 0)
        {
            _diags.Add(new CodeGenDiagnostic(span, "CG5001", CodeGenSeverity.Warning,
                Msg.Diag("CG5001", fnName)));
            _il.Append(_il.Create(OpCodes.Ldnull));
            return;
        }

        // Generate a display class: __AuraDerivOf_{fnName} with a field per op
        var displayClassName = $"__AuraDerivOf_{fnName}";
        var existing = _auraModule.NestedTypes.FirstOrDefault(t => t.Name == displayClassName);

        if (existing is null)
        {
            existing = new TypeDefinition("", displayClassName,
                TA.NestedPublic | TA.Class | TA.Sealed | TA.BeforeFieldInit,
                _module.TypeSystem.Object);

            // Default .ctor
            var dCtor = new MethodDefinition(".ctor",
                MA.Public | MA.HideBySig | MA.SpecialName | MA.RTSpecialName,
                _module.TypeSystem.Void);
            existing.Methods.Add(dCtor);
            var ctorIl = dCtor.Body.GetILProcessor();
            ctorIl.Append(ctorIl.Create(OpCodes.Ldarg_0));
            var objCtor = _module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            ctorIl.Append(ctorIl.Create(OpCodes.Call, objCtor));
            ctorIl.Append(ctorIl.Create(OpCodes.Ret));

            // Add fields for each op (strip prefix to get short name)
            foreach (var prop in opProps)
            {
                var shortName = prop.Name[prefix.Length..]; // e.g., "validate", "transform"
                var fieldType = prop.PropertyType ?? _module.TypeSystem.Object;
                var field = new FieldDefinition(shortName,
                    FA.Public,
                    fieldType);
                existing.Fields.Add(field);
            }

            _auraModule.NestedTypes.Add(existing);
        }

        // Emit: var d = new __AuraDerivOf_{fnName}(); d.op1 = this.__aura_op_{fn}_{op1}; ... push d
        var displayCtor = existing.Methods.First(m => m.Name == ".ctor");
        _il.Append(_il.Create(OpCodes.Newobj, displayCtor));

        for (int i = 0; i < opProps.Count; i++)
        {
            var shortName = opProps[i].Name[prefix.Length..];
            var field = existing.Fields.First(f => f.Name == shortName);
            var getter = opProps[i].GetMethod;

            _il.Append(_il.Create(OpCodes.Dup)); // keep ref on stack
            if (getter is not null)
            {
                if (getter.IsStatic)
                {
                    _il.Append(_il.Create(OpCodes.Call, _module.ImportReference(getter)));
                }
                else
                {
                    _il.Append(_il.Create(OpCodes.Ldarg_0));
                    _il.Append(_il.Create(OpCodes.Callvirt, _module.ImportReference(getter)));
                }
            }
            else
            {
                _il.Append(_il.Create(OpCodes.Ldnull));
            }
            _il.Append(_il.Create(OpCodes.Stfld, field));
        }
        // Display class instance remains on stack
    }

    private void EmitDelegateInvoke(CallExprNode call)
    {
        EmitExpr(call.Callee, expected: null);
        var delType = InferExprType(call.Callee);

        foreach (var a in call.Args)
        {
            if (a is PositionalArgNode pa) EmitExpr(pa.Value, expected: null);
            else if (a is NamedArgNode na) EmitExpr(na.Value, expected: null);
        }

        var invoke = TryFindDelegateInvoke(delType);
        if (invoke is null)
        {
            _diags.Add(new CodeGenDiagnostic(call.Span, "CG3302", CodeGenSeverity.Error, Msg.Diag("CG3302", delType.FullName)));
            return;
        }

        _il.Append(_il.Create(OpCodes.Callvirt, invoke));
    }

    private MethodReference? TryFindDelegateInvoke(TypeReference delType)
    {
        var rt = ResolveClrTypeFromTypeReference(delType);
        if (rt is null) return null;
        var mi = rt.GetMethod("Invoke");
        if (mi is null) return null;
        return _module.ImportReference(mi);
    }

private void EmitSeqExpr(SeqExprNode se, TypeReference expected)
{
    // Evaluate each expression in order, discarding intermediate values.
    if (se.Exprs.Count == 0)
        return;

    for (var i = 0; i < se.Exprs.Count; i++)
    {
        var expr = se.Exprs[i];
        var isLast = i == se.Exprs.Count - 1;

        if (isLast)
        {
            EmitExpr(expr, expected);
            continue;
        }

        var t = InferExprType(expr);
        EmitExpr(expr, t);

        if (t.FullName != _module.TypeSystem.Void.FullName)
            _il.Emit(OpCodes.Pop);
    }
}

    private void EmitLetExpr(LetExprNode le, TypeReference? expected)
    {
        EnterScope();

        foreach (var d in le.Decls)
        {
            var name = d.Name.Text;

            var type = d.Type is not null
                ? ResolveType(_module, d.Type, _imports, _userTypes, _genericContext, _diags, d.Span)
                : d.Init is not null ? InferExprType(d.Init) : _module.TypeSystem.Object;

            var local = DeclareLocal(name, type);

            if (d.Init is not null)
            {
                EmitExpr(d.Init, expected: type);
                CoerceTopIfNeeded(d.Init.Span, InferExprType(d.Init), type);
                _il.Append(_il.Create(OpCodes.Stloc, local));
            }
        }

        EmitExpr(le.Body, expected: expected);

        ExitScope();
    }

    private void EmitTryCatchExpr(TryCatchExprNode te, TypeReference? expected)
    {
        var resultType = expected ?? InferExprType(te.TryExpr);
        if (resultType.MetadataType == MetadataType.Void)
        {
            _diags.Add(new CodeGenDiagnostic(te.Span, "CG3400", CodeGenSeverity.Error,
                Msg.Diag("CG3400")));
            _il.Append(_il.Create(OpCodes.Ldnull));
            return;
        }

        var tmp = new VariableDefinition(resultType);
        _method.Body.Variables.Add(tmp);

        var tryStart = _il.Create(OpCodes.Nop);
        _il.Append(tryStart);

        EmitExpr(te.TryExpr, expected: resultType);
        CoerceTopIfNeeded(te.TryExpr.Span, InferExprType(te.TryExpr), resultType);
        _il.Append(_il.Create(OpCodes.Stloc, tmp));

        var endLabel = _il.Create(OpCodes.Nop);
        _il.Append(_il.Create(OpCodes.Leave, endLabel));

        var handlerStarts = new List<Instruction>();
        for (int i = 0; i < te.Catches.Count; i++)
        {
            var c = te.Catches[i];

            var hs = _il.Create(OpCodes.Nop);
            handlerStarts.Add(hs);
            _il.Append(hs);

            var catchType = c.Type is null
                ? _module.ImportReference(typeof(Exception))
                : ResolveType(_module, c.Type, _imports, _userTypes, _genericContext, _diags, c.Span);

            var exName = c.Name?.Text ?? FreshTempName("__aura_ex");
            var exVar = DeclareLocal(exName, catchType);
            _il.Append(_il.Create(OpCodes.Stloc, exVar));

            EnterScope();
            _scope.Locals[exName] = exVar;

            EmitExpr(c.Body, expected: resultType);
            CoerceTopIfNeeded(c.Body.Span, InferExprType(c.Body), resultType);
            _il.Append(_il.Create(OpCodes.Stloc, tmp));

            ExitScope();

            _il.Append(_il.Create(OpCodes.Leave, endLabel));
        }

        _il.Append(endLabel);
        _il.Append(_il.Create(OpCodes.Ldloc, tmp));

        var tryEnd = handlerStarts.Count > 0 ? handlerStarts[0] : endLabel;

        for (int i = 0; i < te.Catches.Count; i++)
        {
            var c = te.Catches[i];
            var hs = handlerStarts[i];
            var he = (i + 1 < handlerStarts.Count) ? handlerStarts[i + 1] : endLabel;

            var catchType = c.Type is null
                ? _module.ImportReference(typeof(Exception))
                : ResolveType(_module, c.Type, _imports, _userTypes, _genericContext, _diags, c.Span);

            _method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = catchType,
                TryStart = tryStart,
                TryEnd = tryEnd,
                HandlerStart = hs,
                HandlerEnd = he
            });
        }
    }

    private void EmitTypeIs(TypeIsExprNode ti)
    {
        EmitExpr(ti.Expr, expected: null);
        var valueType = InferExprType(ti.Expr);

        var targetType = ResolveType(_module, ti.Type, _imports, _userTypes, _genericContext, _diags, ti.Span);
        if (valueType.IsValueType) _il.Append(_il.Create(OpCodes.Box, valueType));

        _il.Append(_il.Create(OpCodes.Isinst, targetType));
        _il.Append(_il.Create(OpCodes.Ldnull));
        _il.Append(_il.Create(OpCodes.Ceq));
        _il.Append(_il.Create(OpCodes.Ldc_I4_0));
        _il.Append(_il.Create(OpCodes.Ceq));
    }

    private void EmitCast(CastExprNode ce)
    {
        EmitExpr(ce.Expr, expected: null);
        var fromType = InferExprType(ce.Expr);
        var toType = ResolveType(_module, ce.Type, _imports, _userTypes, _genericContext, _diags, ce.Span);

        if (toType.IsValueType)
        {
            if (!fromType.IsValueType)
                _il.Append(_il.Create(OpCodes.Unbox_Any, toType));
        }
        else
        {
            _il.Append(_il.Create(OpCodes.Castclass, toType));
        }
    }

    private void EmitLambda(LambdaExprNode lam, TypeReference? expected)
    {
        // v2: closure capturing via display class
        var delType = expected ?? InferLambdaDelegateType(lam);

        var delClr = ResolveClrTypeFromTypeReference(delType);
        if (delClr is null)
        {
            _diags.Add(new CodeGenDiagnostic(lam.Span, "CG3501", CodeGenSeverity.Error, Msg.Diag("CG3501", delType.FullName)));
            _il.Append(_il.Create(OpCodes.Ldnull));
            return;
        }

        var invoke = delClr.GetMethod("Invoke");
        var ctor = delClr.GetConstructor(new[] { typeof(object), typeof(IntPtr) });
        if (invoke is null || ctor is null)
        {
            _diags.Add(new CodeGenDiagnostic(lam.Span, "CG3502", CodeGenSeverity.Error,
                Msg.Diag("CG3502", delClr.FullName ?? delClr.Name)));
            _il.Append(_il.Create(OpCodes.Ldnull));
            return;
        }
        var invokeParams = invoke.GetParameters();

        var captures = FindCapturedLocals(lam);

        if (captures.Count == 0)
        {
            EmitNonCapturingLambda(lam, delClr, invoke, ctor, invokeParams);
            return;
        }

        EmitCapturingLambda(lam, delClr, invoke, ctor, invokeParams, captures);
    }

    private void EmitNonCapturingLambda(LambdaExprNode lam, Type delClr, MethodInfo invoke, ConstructorInfo ctor, ParameterInfo[] invokeParams)
    {
        var implName = FreshTempName("__lambda");
        var impl = new MethodDefinition(
            implName,
            MA.Private | MA.Static | MA.HideBySig,
            _module.ImportReference(invoke.ReturnType)
        );

        foreach (var p in invokeParams)
            impl.Parameters.Add(new ParameterDefinition(p.Name ?? "p", PA.None, _module.ImportReference(p.ParameterType)));

        _auraModule.Methods.Add(impl);

        var implEmitter = new CecilEmitter(_module, _auraModule, impl, _imports, _topLevelMethods, _userTypes, _diags);
        implEmitter.EnterScope();

        var paramNames = lam.Parameters.Select(p => p.Name.Text).ToArray();
        for (int i = 0; i < Math.Min(paramNames.Length, impl.Parameters.Count); i++)
            implEmitter._args[paramNames[i]] = impl.Parameters[i];

        if (impl.ReturnType.MetadataType == MetadataType.Void)
        {
            implEmitter.EmitExpr(lam.Body, expected: null);
            if (implEmitter.InferExprType(lam.Body).MetadataType != MetadataType.Void)
                implEmitter._il.Append(implEmitter._il.Create(OpCodes.Pop));
            implEmitter._il.Append(implEmitter._il.Create(OpCodes.Ret));
        }
        else
        {
            implEmitter.EmitExpr(lam.Body, expected: impl.ReturnType);
            implEmitter.CoerceTopIfNeeded(lam.Body.Span, implEmitter.InferExprType(lam.Body), impl.ReturnType);
            implEmitter._il.Append(implEmitter._il.Create(OpCodes.Ret));
        }

        implEmitter.ExitScope();

        _il.Append(_il.Create(OpCodes.Ldnull));
        _il.Append(_il.Create(OpCodes.Ldftn, impl));
        var ctorRef = _module.ImportReference(ctor);
        _il.Append(_il.Create(OpCodes.Newobj, ctorRef));
    }

    private void EmitCapturingLambda(
        LambdaExprNode lam,
        Type delClr,
        MethodInfo invoke,
        ConstructorInfo ctor,
        ParameterInfo[] invokeParams,
        HashSet<string> captures)
    {
        // Display class is nested into AuraModule (same as v2)
        var dcName = $"<>DisplayClass{_displayClassId++}_{_method.Name}";
        var dc = new TypeDefinition(
            "",
            dcName,
            TA.NestedPrivate | TA.Sealed | TA.BeforeFieldInit | TA.Class,
            _module.TypeSystem.Object
        );
        _auraModule.NestedTypes.Add(dc);

        // .ctor
        var dcCtor = new MethodDefinition(".ctor",
            MA.Public | MA.HideBySig | MA.SpecialName | MA.RTSpecialName,
            _module.TypeSystem.Void);
        dc.Methods.Add(dcCtor);
        var cil = dcCtor.Body.GetILProcessor();
        cil.Append(cil.Create(OpCodes.Ldarg_0));
        var objCtorInfo = typeof(object).GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException("System.Object parameterless constructor not found.");
        var objCtor = _module.ImportReference(objCtorInfo);
        cil.Append(cil.Create(OpCodes.Call, objCtor));
        cil.Append(cil.Create(OpCodes.Ret));

        // captured fields
        var fieldMap = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var name in captures)
        {
            var t = LookupLocal(name)?.VariableType ?? LookupArg(name)?.ParameterType ?? _module.TypeSystem.Object;
            var f = new FieldDefinition(name, FA.Public, t);
            dc.Fields.Add(f);
            fieldMap[name] = f;
        }

        // instance method
        var implName = FreshTempName("__lambda");
        var impl = new MethodDefinition(
            implName,
            MA.Public | MA.HideBySig | MA.Virtual | MA.Final | MA.NewSlot,
            _module.ImportReference(invoke.ReturnType)
        );
        foreach (var p in invokeParams)
            impl.Parameters.Add(new ParameterDefinition(p.Name ?? "p", PA.None, _module.ImportReference(p.ParameterType)));
        dc.Methods.Add(impl);

        var closureRefs = fieldMap.ToDictionary(kv => kv.Key, kv => (FieldReference)kv.Value, StringComparer.Ordinal);

        var implEmitter = new CecilEmitter(_module, _auraModule, impl, _imports, _topLevelMethods, _userTypes, _diags,
            closureFields: closureRefs, isLambdaDisplayInstanceMethod: true);

        implEmitter.EnterScope();

        var paramNames = lam.Parameters.Select(p => p.Name.Text).ToArray();
        for (int i = 0; i < Math.Min(paramNames.Length, impl.Parameters.Count); i++)
            implEmitter._args[paramNames[i]] = impl.Parameters[i];

        if (impl.ReturnType.MetadataType == MetadataType.Void)
        {
            implEmitter.EmitExpr(lam.Body, expected: null);
            if (implEmitter.InferExprType(lam.Body).MetadataType != MetadataType.Void)
                implEmitter._il.Append(implEmitter._il.Create(OpCodes.Pop));
            implEmitter._il.Append(implEmitter._il.Create(OpCodes.Ret));
        }
        else
        {
            implEmitter.EmitExpr(lam.Body, expected: impl.ReturnType);
            implEmitter.CoerceTopIfNeeded(lam.Body.Span, implEmitter.InferExprType(lam.Body), impl.ReturnType);
            implEmitter._il.Append(implEmitter._il.Create(OpCodes.Ret));
        }

        implEmitter.ExitScope();

        // instantiate display class and set fields (value-capture)
        var dcLocal = new VariableDefinition(dc);
        _method.Body.Variables.Add(dcLocal);

        _il.Append(_il.Create(OpCodes.Newobj, dcCtor));
        _il.Append(_il.Create(OpCodes.Stloc, dcLocal));

        foreach (var kv in fieldMap)
        {
            var capName = kv.Key;
            var field = kv.Value;

            _il.Append(_il.Create(OpCodes.Ldloc, dcLocal));

            var loc = LookupLocal(capName);
            if (loc is not null)
            {
                _il.Append(_il.Create(OpCodes.Ldloc, loc));
                CoerceTopIfNeeded(lam.Span, loc.VariableType, field.FieldType);
            }
            else
            {
                var arg = LookupArg(capName);
                if (arg is not null)
                {
                    EmitLdarg(arg);
                    CoerceTopIfNeeded(lam.Span, arg.ParameterType, field.FieldType);
                }
                else if (_isInstanceMethod)
                {
                    // allow capturing this member by name (value copy)
                    var f2 = _declaringType.Fields.FirstOrDefault(ff => ff.Name == capName);
                    if (f2 is not null)
                    {
                        _il.Append(_il.Create(OpCodes.Ldarg_0));
                        _il.Append(_il.Create(OpCodes.Ldfld, f2));
                        CoerceTopIfNeeded(lam.Span, f2.FieldType, field.FieldType);
                    }
                    else
                    {
                        _il.Append(_il.Create(OpCodes.Ldnull));
                    }
                }
                else
                {
                    _il.Append(_il.Create(OpCodes.Ldnull));
                }
            }

            _il.Append(_il.Create(OpCodes.Stfld, field));
        }

        // delegate newobj (target, ldftn)
        _il.Append(_il.Create(OpCodes.Ldloc, dcLocal));
        _il.Append(_il.Create(OpCodes.Ldftn, impl));
        var ctorRef = _module.ImportReference(ctor);
        _il.Append(_il.Create(OpCodes.Newobj, ctorRef));
    }

    private HashSet<string> FindCapturedLocals(LambdaExprNode lam)
    {
        var paramSet = new HashSet<string>(lam.Parameters.Select(p => p.Name.Text), StringComparer.Ordinal);
        var captures = new HashSet<string>(StringComparer.Ordinal);

        void Walk(ExprNode e)
        {
            switch (e)
            {
                case NameExprNode ne:
                    if (!paramSet.Contains(ne.Name.Text))
                    {
                        if (LookupLocal(ne.Name.Text) is not null || LookupArg(ne.Name.Text) is not null)
                            captures.Add(ne.Name.Text);
                    }
                    break;

                case LambdaExprNode:
                    break;

                default:
                    foreach (var child in EnumerateChildExpr(e))
                        Walk(child);
                    break;
            }
        }

        Walk(lam.Body);
        return captures;
    }

    private IEnumerable<ExprNode> EnumerateChildExpr(ExprNode e)
    {
        switch (e)
        {
            case UnaryExprNode u:
                yield return u.Operand; yield break;
            case BinaryExprNode b:
                yield return b.Left; yield return b.Right; yield break;
            case ConditionalExprNode c:
                yield return c.Condition; yield return c.Then; yield return c.Else; yield break;
            case AssignmentExprNode a:
                yield return a.Left; yield return a.Right; yield break;
            case CallExprNode c:
                yield return c.Callee;
                foreach (var arg in c.Args)
                {
                    if (arg is PositionalArgNode pa) yield return pa.Value;
                    else if (arg is NamedArgNode na) yield return na.Value;
                }
                yield break;
            case MemberAccessExprNode ma:
                yield return ma.Target; yield break;
            case InterpolatedStringExprNode isx:
                foreach (var p in isx.Parts)
                    if (p is InterpExprPartNode ep) yield return ep.Expr;
                yield break;
            case LetExprNode le:
                foreach (var d in le.Decls) { if (d.Init is not null) yield return d.Init; }
                yield return le.Body;
                yield break;
            case TryCatchExprNode te:
                yield return te.TryExpr;
                foreach (var c in te.Catches) yield return c.Body;
                yield break;
            case CastExprNode ce:
                yield return ce.Expr; yield break;
            case TypeIsExprNode ti:
                yield return ti.Expr; yield break;
            default:
                yield break;
        }
    }

    /* =========================
     *  Call resolution
     * ========================= */

    private enum CallKind { Static, Instance }

    private MethodReference? TryResolveCallTarget(CallExprNode call, out CallKind kind, out ExprNode? instanceExpr, out bool implicitThis)
    {
        kind = CallKind.Static;
        instanceExpr = null;
        implicitThis = false;

        // 1) Top-level function call
        if (call.Callee is NameExprNode ne && _topLevelMethods.TryGetValue(ne.Name.Text, out var top))
        {
            kind = CallKind.Static;
            return _module.ImportReference(top);
        }

        // 2) Implicit this call: foo(...)
        if (call.Callee is NameExprNode ne2 && _isInstanceMethod)
        {
            var cand = ResolveUserTypeMethodOverload(_declaringType, ne2.Name.Text, call.Args);
            if (cand is not null)
            {
                kind = CallKind.Instance;
                implicitThis = true;
                return _module.ImportReference(cand);
            }
        }

        // 3) Member access call: target.member(...)
        if (call.Callee is MemberAccessExprNode ma)
        {
            // Static on user-defined type?
            if (ma.Target is NameExprNode tn1 && _userTypes.TryGetValue(tn1.Name.Text, out var udt))
            {
                var md = ResolveUserTypeMethodOverload(udt, ma.Member.Text, call.Args);
                if (md is not null)
                {
                    kind = CallKind.Static;
                    return _module.ImportReference(md);
                }
            }

            // Static CLR type call?
            if (ma.Target is NameExprNode typeName &&
                LookupLocal(typeName.Name.Text) is null &&
                LookupArg(typeName.Name.Text) is null)
            {
                var t = TryResolveClrType(typeName.Name.Text, _imports);
                if (t is not null)
                {
                    var mi = ResolveMethodOverloadByScore(t, ma.Member.Text, call.Args, isStatic: true);
                    if (mi is null) return null;
                    kind = CallKind.Static;
                    return ImportMethodReferenceWithGenericArgs(mi, call);
                }
            }

            // Instance call
            instanceExpr = ma.Target;

            // If target is a user-defined type local, try resolve within Cecil types
            var targetTypeRef = InferExprType(ma.Target);
            if (targetTypeRef is TypeDefinition td && _userTypes.Values.Contains(td))
            {
                var md = ResolveUserTypeMethodOverload(td, ma.Member.Text, call.Args);
                if (md is not null)
                {
                    kind = CallKind.Instance;
                    return _module.ImportReference(md);
                }
            }

            // Fallback to CLR reflection for BCL types
            var targetClr = ResolveClrTypeFromExpr(ma.Target);
            if (targetClr is null) return null;

            var mi2 = ResolveMethodOverloadByScore(targetClr, ma.Member.Text, call.Args, isStatic: false);
            if (mi2 is null) return null;

            kind = CallKind.Instance;
            return ImportMethodReferenceWithGenericArgs(mi2, call);
        }

        return null;
    }

    /// <summary>
    /// Imports a MethodInfo into Cecil and (when possible) closes generic method definitions using either
    /// explicit type arguments in the AST (member access type args) or simple inference from call arguments.
    ///
    /// This is primarily needed for LINQ calls produced by lowering (e.g. System.Linq.Enumerable.Where).
    /// </summary>
    private MethodReference ImportMethodReferenceWithGenericArgs(MethodInfo mi, CallExprNode call)
    {
        var mr = _module.ImportReference(mi);

        if (!mi.IsGenericMethodDefinition)
            return mr;

        // 1) Explicit type arguments: Foo.Bar<T1, T2>(...)
        List<TypeReference>? typeArgs = null;
        if (call.Callee is MemberAccessExprNode ma && ma.TypeArgs.Count > 0)
        {
            typeArgs = ma.TypeArgs.Select(ResolveType).ToList();
        }

        // 2) Simple inference (best-effort; enough for Enumerable.Where/Select/etc.).
        typeArgs ??= TryInferGenericArguments(mi, call);

        var genCount = mi.GetGenericArguments().Length;

        // 3) Last-resort: close with <object, object, ...> so IL stays valid.
        if (typeArgs is null)
        {
            _diags.Add(new CodeGenDiagnostic(call.Span, "CG5105", CodeGenSeverity.Warning,
                Msg.Diag("CG5105", mi.DeclaringType?.FullName ?? "?", mi.Name)));
            typeArgs = Enumerable.Repeat(_module.TypeSystem.Object, genCount).ToList();
        }
        if (typeArgs.Count != genCount)
            return mr;

        var gim = new GenericInstanceMethod(mr);
        foreach (var ta in typeArgs)
            gim.GenericArguments.Add(ta);
        return gim;
    }

    private List<TypeReference>? TryInferGenericArguments(MethodInfo mi, CallExprNode call)
    {
        var genArgs = mi.GetGenericArguments();
        if (genArgs.Length == 0)
            return null;

        // Heuristic: If the method is <T>(IEnumerable<T> source, ...), infer T from the first argument.
        if (genArgs.Length == 1 && call.Args.Count >= 1)
        {
            var p0 = mi.GetParameters().FirstOrDefault()?.ParameterType;
            if (p0 is not null && p0.IsGenericType)
            {
                var def = p0.GetGenericTypeDefinition();
                if (def == typeof(IEnumerable<>))
                {
                    var arg0Expr = call.Args[0] is PositionalArgNode pa0 ? pa0.Value : null;
                    if (arg0Expr is null) return null;
                    var arg0Ty = InferExprType(arg0Expr);
                    var elem = TryGetEnumerableElementType(arg0Ty) ?? _module.TypeSystem.Object;
                    return new List<TypeReference> { elem };
                }
            }

            // Fallback: if the first arg is a single-generic-arg type (List<T>, HashSet<T>, ...), use that.
            var arg0ExprFallback = call.Args[0] is PositionalArgNode pa0f ? pa0f.Value : null;
            if (arg0ExprFallback is null) return null;
            var fallback = InferExprType(arg0ExprFallback);
            if (fallback is GenericInstanceType git && git.GenericArguments.Count == 1)
            {
                return new List<TypeReference> { git.GenericArguments[0] };
            }
        }

        return null;
    }

    private TypeReference? TryGetEnumerableElementType(TypeReference seqType)
    {
        // Arrays
        if (seqType is ArrayType at)
            return at.ElementType;

        // Already IEnumerable<T>
        if (seqType is GenericInstanceType git)
        {
            if (git.ElementType.FullName == "System.Collections.Generic.IEnumerable`1" && git.GenericArguments.Count == 1)
                return git.GenericArguments[0];
        }

        // Try via CLR reflection when possible (robust for BCL collections).
        var clr = ResolveClrTypeFromTypeReference(seqType);
        if (clr is not null)
        {
            if (clr.IsArray)
                return _module.ImportReference(clr.GetElementType()!);

            var ienum = clr.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

            if (ienum is not null)
                return _module.ImportReference(ienum.GetGenericArguments()[0]);
        }

        return null;
    }

    private MethodReference? ResolveUserTypeMethodOverload(TypeDefinition td, string name, IReadOnlyList<ArgumentNode> args)
    {
        // Walk up the inheritance chain for method resolution
        var search = td;
        while (search is not null)
        {
            var candidates = search.Methods.Where(m => m.Name == name && m.Parameters.Count == args.Count).ToArray();
            if (candidates.Length > 0)
            {
                var pub = candidates.FirstOrDefault(m => m.IsPublic);
                return pub ?? candidates[0];
            }
            var baseRef = search.BaseType;
            if (baseRef is null) break;
            _userTypes.TryGetValue(baseRef.Name, out search);
        }
        return null;
    }

    // Improved overload selection for CLR methods (v2)
    private MethodInfo? ResolveMethodOverloadByScore(Type t, string name, IReadOnlyList<ArgumentNode> args, bool isStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var candidates = t.GetMethods(flags)
            .Where(m => m.Name == name && m.GetParameters().Length == args.Count)
            .ToArray();

        if (candidates.Length == 0) return null;
        if (candidates.Length == 1) return candidates[0];

        var argExprs = args.Select(a => a switch
        {
            PositionalArgNode pa => pa.Value,
            NamedArgNode na => na.Value,
            _ => null
        }).ToArray();

        var best = (MethodInfo?)null;
        var bestScore = int.MaxValue;

        foreach (var m in candidates)
        {
            var ps = m.GetParameters();
            var score = 0;
            var ok = true;

            for (int i = 0; i < ps.Length; i++)
            {
                var aexpr = argExprs[i];
                var ptype = ps[i].ParameterType;

                var s = ScoreArgument(aexpr, ptype);
                if (s == int.MaxValue)
                {
                    ok = false;
                    break;
                }
                score += s;
            }

            if (!ok) continue;

            if (score < bestScore || (score == bestScore && best is not null && !best.IsPublic && m.IsPublic))
            {
                best = m;
                bestScore = score;
            }
        }

        return best;
    }

    private int ScoreArgument(ExprNode? argExpr, Type paramType)
    {
        if (argExpr is null) return int.MaxValue;

        if (argExpr is LiteralExprNode lit && lit.Kind == LiteralKind.Null)
        {
            return (!paramType.IsValueType || Nullable.GetUnderlyingType(paramType) is not null) ? 0 : int.MaxValue;
        }

        var argTypeRef = InferExprType(argExpr);
        var argClr = ResolveClrTypeFromTypeReference(argTypeRef);

        if (argClr is null)
            return paramType == typeof(object) ? 10 : int.MaxValue;

        if (argClr == paramType) return 0;

        if (!argClr.IsValueType && !paramType.IsValueType && paramType.IsAssignableFrom(argClr))
            return 1;

        if (argClr.IsValueType && paramType == typeof(object))
            return 3;

        if (IsNumericWidening(argClr, paramType))
            return 2;

        if (paramType == typeof(object))
            return 9;

        if (!argClr.IsValueType && !paramType.IsValueType)
            return 6;

        return int.MaxValue;
    }

    private static bool IsNumericWidening(Type from, Type to)
    {
        if (from == typeof(int) && to == typeof(long)) return true;
        if (from == typeof(short) && (to == typeof(int) || to == typeof(long))) return true;
        if (from == typeof(sbyte) && (to == typeof(short) || to == typeof(int) || to == typeof(long))) return true;

        if (from == typeof(uint) && to == typeof(ulong)) return true;
        if (from == typeof(ushort) && (to == typeof(uint) || to == typeof(ulong))) return true;
        if (from == typeof(byte) && (to == typeof(ushort) || to == typeof(uint) || to == typeof(ulong))) return true;

        if (from == typeof(float) && to == typeof(double)) return true;
        return false;
    }

    private object? TryResolveMember(MemberAccessExprNode ma, out bool isStatic, out Type? targetType, out string memberName)
    {
        isStatic = false;
        targetType = null;
        memberName = ma.Member.Text;

        if (ma.Target is NameExprNode typeName &&
            LookupLocal(typeName.Name.Text) is null &&
            LookupArg(typeName.Name.Text) is null)
        {
            var t = TryResolveClrType(typeName.Name.Text, _imports);
            if (t is not null)
            {
                isStatic = true;
                targetType = t;
                return ResolveClrMember(t, memberName, isStatic: true);
            }
        }

        var targetClr = ResolveClrTypeFromExpr(ma.Target);
        if (targetClr is null) return null;
        isStatic = false;
        targetType = targetClr;
        return ResolveClrMember(targetClr, memberName, isStatic: false);
    }

    private static object? ResolveClrMember(Type t, string name, bool isStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var p = t.GetProperty(name, flags);
        if (p is not null) return p;

        var f = t.GetField(name, flags);
        if (f is not null) return f;

        var m = t.GetMethod(name, flags);
        if (m is not null) return m;

        return null;
    }

    private Type? ResolveClrTypeFromExpr(ExprNode e)
    {
        var tr = InferExprType(e);
        return ResolveClrTypeFromTypeReference(tr);
    }

    private static Type? ResolveClrTypeFromTypeReference(TypeReference tr)
    {
        try
        {
            if (tr is GenericInstanceType git)
            {
                var def = ResolveClrTypeFromTypeReference(git.ElementType);
                if (def is null) return null;

                var args = git.GenericArguments
                    .Select(ResolveClrTypeFromTypeReference)
                    .ToArray();

                if (args.Any(a => a is null)) return null;
                return def.MakeGenericType(args!);
            }

            var full = tr.FullName.Replace("/", "+");
            var t = Type.GetType(full);
            if (t is not null) return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(full);
                    if (t is not null) return t;
                }
                catch (Exception ex) when (ex is TypeLoadException or ReflectionTypeLoadException or System.IO.FileNotFoundException or BadImageFormatException)
                {
                    // Expected: assembly may contain types we can't load; skip silently.
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is TypeLoadException or ReflectionTypeLoadException or ArgumentException or InvalidOperationException)
        {
            // Expected: generic type construction or malformed type reference.
            return null;
        }
    }

    
private static Type? TryResolveClrType(string simpleOrQualifiedName, IEnumerable<string> importedNamespaces, int? arity = null)
{
    static string MakeNameWithArity(string n, int? a)
        => (a is null || n.Contains('`')) ? n : $"{n}`{a.Value}";

    static Type? SearchAssemblies(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(typeName);
                if (t is not null) return t;
            }
            catch (Exception ex) when (ex is TypeLoadException or ReflectionTypeLoadException or System.IO.FileNotFoundException or BadImageFormatException)
            {
                // Expected: assembly may contain types we can't load; skip silently.
            }
        }
        return null;
    }

    // Fully-qualified (explicit namespace).
    if (simpleOrQualifiedName.Contains('.'))
    {
        var fq = MakeNameWithArity(simpleOrQualifiedName, arity);
        var t = Type.GetType(fq) ?? SearchAssemblies(fq);
        if (t is not null) return t;
    }

    // Imported namespaces.
    foreach (var ns in importedNamespaces)
    {
        var q = MakeNameWithArity(ns + "." + simpleOrQualifiedName, arity);
        var t = Type.GetType(q) ?? SearchAssemblies(q);
        if (t is not null) return t;
    }

    return null;
}



    /* =========================
     *  Types
     * ========================= */

    
public static TypeReference ResolveType(
    ModuleDefinition module,
    TypeNode type,
    IEnumerable<string> importedNamespaces,
    IReadOnlyDictionary<string, TypeDefinition> userTypes,
    List<CodeGenDiagnostic> diags,
    SourceSpan span)
    => ResolveType(module, type, importedNamespaces, userTypes, genericContext: null, diags, span);

public static TypeReference ResolveType(
    ModuleDefinition module,
    TypeNode type,
    IEnumerable<string> importedNamespaces,
    IReadOnlyDictionary<string, TypeDefinition> userTypes,
    IReadOnlyDictionary<string, TypeReference>? genericContext,
    List<CodeGenDiagnostic> diags,
    SourceSpan span)
{
    // Generic parameter lookup (method/type)
    if (type is NamedTypeNode nt0)
    {
        var qn0 = nt0.Name.ToString();
        if (genericContext is not null && nt0.TypeArgs.Count == 0 && genericContext.TryGetValue(qn0, out var gp0))
            return gp0;
    }

    return type switch
    {
        BuiltinTypeNode b => b.Kind switch
        {
            BuiltinTypeKind.I8 => module.TypeSystem.SByte,
            BuiltinTypeKind.I16 => module.TypeSystem.Int16,
            BuiltinTypeKind.I32 => module.TypeSystem.Int32,
            BuiltinTypeKind.I64 => module.TypeSystem.Int64,
            BuiltinTypeKind.U8 => module.TypeSystem.Byte,
            BuiltinTypeKind.U16 => module.TypeSystem.UInt16,
            BuiltinTypeKind.U32 => module.TypeSystem.UInt32,
            BuiltinTypeKind.U64 => module.TypeSystem.UInt64,
            BuiltinTypeKind.F32 => module.TypeSystem.Single,
            BuiltinTypeKind.F64 => module.TypeSystem.Double,
            BuiltinTypeKind.Decimal => module.ImportReference(typeof(decimal)),
            BuiltinTypeKind.Bool => module.TypeSystem.Boolean,
            BuiltinTypeKind.Char => module.TypeSystem.Char,
            BuiltinTypeKind.String => module.TypeSystem.String,
            BuiltinTypeKind.Object => module.TypeSystem.Object,
            BuiltinTypeKind.Void => module.TypeSystem.Void,
            _ => module.TypeSystem.Object
        },

        NullableTypeNode n =>
            module.ImportReference(typeof(Nullable<>)).MakeGenericInstanceType(
                ResolveType(module, n.Inner, importedNamespaces, userTypes, genericContext, diags, span)),

        NamedTypeNode nt =>
            ResolveNamedType(module, nt, importedNamespaces, userTypes, genericContext, diags, span),

        FunctionTypeNode ft =>
            ResolveDelegateType(module, ft, importedNamespaces, userTypes, genericContext, diags, span),

        WindowOfTypeNode w =>
            ResolveType(module, w.Inner, importedNamespaces, userTypes, genericContext, diags, span),

        _ => module.TypeSystem.Object
    };
}

private static TypeReference ResolveNamedType(
    ModuleDefinition module,
    NamedTypeNode nt,
    IEnumerable<string> importedNamespaces,
    IReadOnlyDictionary<string, TypeDefinition> userTypes,
    IReadOnlyDictionary<string, TypeReference>? genericContext,
    List<CodeGenDiagnostic> diags,
    SourceSpan span)
{
    var qn = nt.Name.ToString();

    // Generic parameter (method/type)
    if (genericContext is not null && nt.TypeArgs.Count == 0 && genericContext.TryGetValue(qn, out var gp))
        return gp;

    // User types
    if (userTypes.TryGetValue(qn, out var udt))
    {
        if (nt.TypeArgs.Count == 0)
            return udt;

        // User-defined generic types not yet supported (best-effort).
        diags.Add(new CodeGenDiagnostic(span, "CG1104", CodeGenSeverity.Warning,
            Msg.Diag("CG1104", qn)));
        return udt;
    }

    // CLR types
    var arity = nt.TypeArgs.Count > 0 ? nt.TypeArgs.Count : (int?)null;
    var clr = TryResolveClrType(qn, importedNamespaces, arity);
    if (clr is null)
    {
        diags.Add(new CodeGenDiagnostic(span, "CG1001", CodeGenSeverity.Error, Msg.Diag("CG1001", qn)));
        return module.TypeSystem.Object;
    }

    var baseRef = module.ImportReference(clr);

    if (nt.TypeArgs.Count == 0)
        return baseRef;

    // Construct generic instance
    var git = new GenericInstanceType(baseRef);
    foreach (var ta in nt.TypeArgs)
        git.GenericArguments.Add(ResolveType(module, ta, importedNamespaces, userTypes, genericContext, diags, ta.Span));
    return git;
}

private static TypeReference ResolveDelegateType(
    ModuleDefinition module,
    FunctionTypeNode ft,
    IEnumerable<string> importedNamespaces,
    IReadOnlyDictionary<string, TypeDefinition> userTypes,
    IReadOnlyDictionary<string, TypeReference>? genericContext,
    List<CodeGenDiagnostic> diags,
    SourceSpan span)
{
    var paramTypes = ft.ParamTypes.Select(p =>
        ResolveType(module, p, importedNamespaces, userTypes, genericContext, diags, span)).ToList();

    var retType = ResolveType(module, ft.ReturnType, importedNamespaces, userTypes, genericContext, diags, span);

    if (retType.MetadataType == MetadataType.Void)
    {
        // Action<...>
        var actClr = ResolveActionClr(paramTypes.Count);
        var actRef = module.ImportReference(actClr);
        if (paramTypes.Count == 0) return actRef;
        var gi = new GenericInstanceType(actRef);
        foreach (var pt in paramTypes) gi.GenericArguments.Add(pt);
        return gi;
    }
    else
    {
        // Func<..., TResult>
        var fnClr = ResolveFuncClr(paramTypes.Count + 1);
        var fnRef = module.ImportReference(fnClr);
        var gi = new GenericInstanceType(fnRef);
        foreach (var pt in paramTypes) gi.GenericArguments.Add(pt);
        gi.GenericArguments.Add(retType);
        return gi;
    }
}


    private TypeReference InferLambdaDelegateType(LambdaExprNode lam)
    {
        var ps = new List<TypeReference>();
        foreach (var p in lam.Parameters)
        {
            if (p.Type is not null)
                ps.Add(ResolveType(_module, p.Type, _imports, _userTypes, _genericContext, _diags, p.Span));
            else
                ps.Add(_module.TypeSystem.Object);
        }

        var ret = InferExprType(lam.Body);
        if (ret.MetadataType == MetadataType.Void)
        {
            var actionClr = ResolveActionClr(ps.Count);
            if (actionClr is null) return _module.TypeSystem.Object;
            var actionRef = _module.ImportReference(actionClr);
            var git = new GenericInstanceType(actionRef);
            foreach (var a in ps) git.GenericArguments.Add(a);
            return git;
        }
        else
        {
            var funcClr = ResolveFuncClr(ps.Count + 1);
            if (funcClr is null) return _module.TypeSystem.Object;
            var funcRef = _module.ImportReference(funcClr);
            var git = new GenericInstanceType(funcRef);
            foreach (var a in ps) git.GenericArguments.Add(a);
            git.GenericArguments.Add(ret);
            return git;
        }
    }

    private static Type? ResolveActionClr(int paramCount)
    {
        return paramCount switch
        {
            0 => typeof(Action),
            1 => typeof(Action<>),
            2 => typeof(Action<,>),
            3 => typeof(Action<,,>),
            4 => typeof(Action<,,,>),
            5 => typeof(Action<,,,,>),
            6 => typeof(Action<,,,,,>),
            7 => typeof(Action<,,,,,,>),
            8 => typeof(Action<,,,,,,,>),
            9 => typeof(Action<,,,,,,,,>),
            10 => typeof(Action<,,,,,,,,,>),
            11 => typeof(Action<,,,,,,,,,,>),
            12 => typeof(Action<,,,,,,,,,,,>),
            13 => typeof(Action<,,,,,,,,,,,,>),
            14 => typeof(Action<,,,,,,,,,,,,,>),
            15 => typeof(Action<,,,,,,,,,,,,,,>),
            16 => typeof(Action<,,,,,,,,,,,,,,,>),
            _ => null
        };
    }

    private static Type? ResolveFuncClr(int typeArgCount)
    {
        return typeArgCount switch
        {
            1 => typeof(Func<>),
            2 => typeof(Func<,>),
            3 => typeof(Func<,,>),
            4 => typeof(Func<,,,>),
            5 => typeof(Func<,,,,>),
            6 => typeof(Func<,,,,,>),
            7 => typeof(Func<,,,,,,>),
            8 => typeof(Func<,,,,,,,>),
            9 => typeof(Func<,,,,,,,,>),
            10 => typeof(Func<,,,,,,,,,>),
            11 => typeof(Func<,,,,,,,,,,>),
            12 => typeof(Func<,,,,,,,,,,,>),
            13 => typeof(Func<,,,,,,,,,,,,>),
            14 => typeof(Func<,,,,,,,,,,,,,>),
            15 => typeof(Func<,,,,,,,,,,,,,,>),
            16 => typeof(Func<,,,,,,,,,,,,,,,>),
            17 => typeof(Func<,,,,,,,,,,,,,,,,>),
            _ => null
        };
    }

    private void EmitDefault(TypeReference t)
    {
        if (!t.IsValueType)
        {
            _il.Append(_il.Create(OpCodes.Ldnull));
            return;
        }

        var tmp = new VariableDefinition(t);
        _method.Body.Variables.Add(tmp);
        _il.Append(_il.Create(OpCodes.Ldloca, tmp));
        _il.Append(_il.Create(OpCodes.Initobj, t));
        _il.Append(_il.Create(OpCodes.Ldloc, tmp));
    }

    internal void CoerceTopIfNeeded(SourceSpan span, TypeReference actual, TypeReference expected)
    {
        if (actual.FullName == expected.FullName)
            return;

        if (expected.FullName == _module.TypeSystem.Object.FullName && actual.IsValueType)
        {
            _il.Append(_il.Create(OpCodes.Box, actual));
            return;
        }

        if (expected.IsValueType && !actual.IsValueType)
        {
            _il.Append(_il.Create(OpCodes.Unbox_Any, expected));
            return;
        }

        if (!expected.IsValueType && !actual.IsValueType)
        {
            _il.Append(_il.Create(OpCodes.Castclass, expected));
            return;
        }

        _diags.Add(new CodeGenDiagnostic(span, "CG4099", CodeGenSeverity.Warning,
            Msg.Diag("CG4099", actual.FullName, expected.FullName)));
    }

    private string FreshTempName(string prefix) => $"{prefix}{_tempId++}";

    private sealed class Scope
    {
        public Scope? Parent { get; }
        public Dictionary<string, VariableDefinition> Locals { get; } = new(StringComparer.Ordinal);
        public ScopeDebugInformation? DebugScope { get; set; }

        public Scope(Scope? parent) => Parent = parent;
    }
}