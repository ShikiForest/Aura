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

internal sealed partial class CecilEmitter
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

}
