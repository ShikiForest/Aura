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
}
