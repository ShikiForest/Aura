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
