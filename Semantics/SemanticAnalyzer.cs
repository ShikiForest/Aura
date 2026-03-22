using System;
using System.Collections.Generic;
using System.Linq;
using AuraLang.Ast;
using AuraLang.I18n;

namespace AuraLang.Semantics;

/// <summary>
/// Aura 语义检查器（v1）：
/// - 声明收集/重名检查
/// - 基础 name binding（局部变量/参数）
/// - 基础类型推导（足够支撑 if/while 条件、??= 左侧可空、switch expression 结果合并等）
/// - 架构约束：禁止 public field、pub property 类型白名单、window 子集、struct 继承限制、trait 实现检查、state function 组检查
/// - 语法级约束补强：item 只能用于谓词索引器；占位符 _ 只能在 pipe 的右侧 stage 使用
/// </summary>
public sealed class SemanticAnalyzer
{
    private readonly List<SemanticDiagnostic> _diags = [];
    private readonly SymbolIndex _index = new();
    private TypeResolver? _typeResolver;

    // 当前分析上下文
    private LocalScope? _scope;
    private bool _inAsyncFunction;
    private string? _currentTypeName;  // enclosing type name (for builder constraint)
    private TypeRef? _currentReturnType; // declared return type of current function

    public SemanticResult Analyze(CompilationUnitNode cu)
    {
        _diags.Clear();
        _index.Types.Clear();
        _index.ImportsByNamespace.Clear();

        // Pass1：收集 declarations + imports
        CollectCompilationUnit(cu);

        _typeResolver = new TypeResolver(_index);

        // Pass2：架构约束与类型级检查
        foreach (var t in _index.AllTypes())
        {
            CheckTypeLevelRules(t);
        }

        // Pass3：函数体检查（语句/表达式）
        AnalyzeBodies(cu);

        return new SemanticResult(_diags);
    }

    /* =========================
     * Pass 1: Declarations
     * ========================= */

    private void CollectCompilationUnit(CompilationUnitNode cu)
    {
        var ns = "";
        EnsureNamespaceImportsList(ns);

        foreach (var item in cu.Items)
            CollectCompilationItem(item, ns);
    }

    private void CollectCompilationItem(ICompilationItem item, string currentNs)
    {
        switch (item)
        {
            case ImportDeclNode imp:
                RegisterImport(currentNs, imp);
                break;

            case NamespaceDeclNode nd:
                var name = nd.Name.ToString();
                var nestedNs = TypeResolver.Combine(currentNs, name);
                EnsureNamespaceImportsList(nestedNs);

                // 继承父命名空间的 imports（符合 using 的直觉）
                InheritImports(currentNs, nestedNs);

                foreach (var m in nd.Members)
                    CollectCompilationItem(m, nestedNs);
                break;

            case TraitDeclNode td:
                RegisterTrait(currentNs, td);
                break;

            case ClassDeclNode cd:
                RegisterType(currentNs, cd, TypeKind.Class);
                break;

            case StructDeclNode sd:
                RegisterType(currentNs, sd, TypeKind.Struct);
                break;

            case EnumDeclNode ed:
                RegisterEnum(currentNs, ed);
                break;

            case WindowDeclNode wd:
                RegisterWindow(currentNs, wd);
                break;

            case FunctionDeclNode fd:
                // 顶层函数：作为一个“类型外函数”，我们不纳入 Types 表（可选）；这里只做重复名检查较困难
                // v1：仅在 body 分析阶段处理
                break;

            default:
                break;
        }
    }

    private void EnsureNamespaceImportsList(string ns)
    {
        if (!_index.ImportsByNamespace.ContainsKey(ns))
            _index.ImportsByNamespace[ns] = [];
    }

    private void InheritImports(string parentNs, string childNs)
    {
        var parent = _index.ImportsByNamespace[parentNs];
        var child = _index.ImportsByNamespace[childNs];

        foreach (var x in parent)
        {
            if (!child.Contains(x, StringComparer.Ordinal))
                child.Add(x);
        }
    }

    private void RegisterImport(string ns, ImportDeclNode imp)
    {
        EnsureNamespaceImportsList(ns);
        var list = _index.ImportsByNamespace[ns];
        var name = imp.Name.ToString();

        if (!list.Contains(name, StringComparer.Ordinal))
            list.Add(name);
    }

    private void RegisterTrait(string ns, TraitDeclNode td)
    {
        var full = TypeResolver.Combine(ns, td.Name.Text);

        if (_index.Types.ContainsKey(full))
        {
            Emit("AUR1010", DiagnosticSeverity.Error, td.Span, Msg.Diag("AUR1010", full));
            return;
        }

        var sym = new TypeSymbol
        {
            FullName = full,
            Kind = TypeKind.Trait,
            Decl = td,
            Namespace = ns
        };

        // trait signatures
        foreach (var m in td.Members)
        {
            var name = m.Name.Text;
            if (!sym.TraitFunctions.TryGetValue(name, out var list))
            {
                list = [];
                sym.TraitFunctions[name] = list;
            }
            list.Add(m);
        }

        _index.Types[full] = sym;
    }

    private void RegisterEnum(string ns, EnumDeclNode ed)
    {
        var full = TypeResolver.Combine(ns, ed.Name.Text);
        if (_index.Types.ContainsKey(full))
        {
            Emit("AUR1010", DiagnosticSeverity.Error, ed.Span, Msg.Diag("AUR1010", full));
            return;
        }

        var sym = new TypeSymbol
        {
            FullName = full,
            Kind = TypeKind.Enum,
            Decl = ed,
            Namespace = ns
        };
        _index.Types[full] = sym;
    }

    private void RegisterWindow(string ns, WindowDeclNode wd)
    {
        var full = TypeResolver.Combine(ns, wd.Name.Text);
        if (_index.Types.ContainsKey(full))
        {
            Emit("AUR1010", DiagnosticSeverity.Error, wd.Span, Msg.Diag("AUR1010", full));
            return;
        }

        var sym = new TypeSymbol
        {
            FullName = full,
            Kind = TypeKind.Window,
            Decl = wd,
            Namespace = ns
        };
        _index.Types[full] = sym;
    }

    private void RegisterType(string ns, TypeDeclNode td, TypeKind kind)
    {
        var full = TypeResolver.Combine(ns, td.Name.Text);

        if (_index.Types.ContainsKey(full))
        {
            Emit("AUR1010", DiagnosticSeverity.Error, td.Span, Msg.Diag("AUR1010", full));
            return;
        }

        var sym = new TypeSymbol
        {
            FullName = full,
            Kind = kind,
            Decl = td,
            Namespace = ns
        };

        sym.BaseTypes.AddRange(td.BaseTypes);

        // members
        foreach (var m in td.Members)
        {
            switch (m)
            {
                case FieldDeclNode f:
                    // field name duplicates
                    if (sym.Fields.ContainsKey(f.Name.Text))
                        Emit("AUR1011", DiagnosticSeverity.Error, f.Span, Msg.Diag("AUR1011", td.Name.Text, f.Name.Text));
                    else
                        sym.Fields[f.Name.Text] = f;
                    break;

                case PropertyDeclNode p:
                    if (sym.Properties.ContainsKey(p.Name.Text))
                        Emit("AUR1011", DiagnosticSeverity.Error, p.Span, Msg.Diag("AUR1011", td.Name.Text, p.Name.Text));
                    else
                        sym.Properties[p.Name.Text] = p;
                    break;

                case FunctionDeclNode fn:
                    if (!sym.Functions.TryGetValue(fn.Name.Text, out var list))
                    {
                        list = [];
                        sym.Functions[fn.Name.Text] = list;
                    }
                    list.Add(fn);
                    break;

                case EnumDeclNode nestedEnum:
                    // v1：不支持 nested types 的语义解析（可扩展）
                    Emit("AUR1012", DiagnosticSeverity.Warning, nestedEnum.Span, Msg.Diag("AUR1012", "enum"));
                    break;

                case WindowDeclNode nestedWindow:
                    Emit("AUR1012", DiagnosticSeverity.Warning, nestedWindow.Span, Msg.Diag("AUR1012", "window"));
                    break;

                default:
                    break;
            }
        }

        _index.Types[full] = sym;
    }

    /* =========================
     * Pass 2: Type-level rules
     * ========================= */

    private void CheckTypeLevelRules(TypeSymbol sym)
    {
        var ctx = new ResolutionContext(sym.Namespace, GetImports(sym.Namespace));

        // AUR4001：禁止 public field
        foreach (var f in sym.Fields.Values)
        {
            if (f.Visibility == Visibility.Public)
                Emit("AUR4001", DiagnosticSeverity.Error, f.Span, Msg.Diag("AUR4001", sym.FullName, f.Name.Text));
        }

        // AUR4002/4003：pub property 类型白名单
        foreach (var p in sym.Properties.Values)
        {
            if (p.Visibility == Visibility.Public)
            {
                var t = _typeResolver!.Resolve(p.Type, ctx);
                if (!IsAllowedPublicPropertyType(t))
                    Emit("AUR4002", DiagnosticSeverity.Error, p.Span, Msg.Diag("AUR4002", sym.FullName, p.Name.Text, t));
            }
        }

        // AUR4020：struct 不能继承 class
        if (sym.Kind == TypeKind.Struct && sym.Decl is StructDeclNode sd)
        {
            foreach (var bt in sd.BaseTypes)
            {
                var tr = _typeResolver!.Resolve(bt, ctx);
                if (tr is TypeRef.Named n && n.ResolvedKind == TypeKind.Class)
                    Emit("AUR4020", DiagnosticSeverity.Error, sd.Span, Msg.Diag("AUR4020", n.FullName));
            }
        }

        // AUR4010/4011：trait 实现检查（class/struct）
        if (sym.Kind is TypeKind.Class or TypeKind.Struct && sym.Decl is TypeDeclNode td)
        {
            foreach (var bt in td.BaseTypes)
            {
                var tr = _typeResolver!.Resolve(bt, ctx);
                if (tr is TypeRef.Named n && n.ResolvedKind == TypeKind.Trait)
                {
                    CheckTraitImplementation(sym, n.FullName);
                }
            }
        }

        // AUR4100/4101：window 子集
        if (sym.Kind == TypeKind.Window && sym.Decl is WindowDeclNode wd)
        {
            CheckWindowSubset(sym.Namespace, wd);
        }

        // AUR2230/2231/2232/2233：状态函数组检查（type 内）
        if (sym.Kind is TypeKind.Class or TypeKind.Struct && sym.Decl is TypeDeclNode td2)
        {
            CheckStateFunctionsInType(sym, td2);
        }

        // AUR4050: constructor prohibition — Aura forbids fn new(...) in class/struct
        CheckNoUserConstructor(sym);

        // AUR4200: self decode function validation
        CheckSelfDecodeFunction(sym);

        // AUR4400: Room.addObject validation — target should implement IRoomReceiver (best-effort check)
        // (Deferred to runtime in v1 — emit warning only)

        // AUR1030: function overload conflict check (State Functions excluded — they share name+params with different StateSpec)
        foreach (var (name, fns) in sym.Functions)
        {
            if (fns.Count < 2) continue;
            var seen = new Dictionary<string, FunctionDeclNode>(StringComparer.Ordinal);
            foreach (var fn in fns)
            {
                // State functions have distinct StateSpec; build a key that includes it
                var key = BuildFunctionSignatureKey(fn, ctx, sym.Namespace);
                if (seen.TryGetValue(key, out _))
                    Emit("AUR1030", DiagnosticSeverity.Error, fn.Span, Msg.Diag("AUR1030", key, sym.FullName));
                else
                    seen[key] = fn;
            }
        }
    }

    private IReadOnlyList<string> GetImports(string ns)
        => _index.ImportsByNamespace.TryGetValue(ns, out var list) ? list : Array.Empty<string>();

    private bool IsAllowedPublicPropertyType(TypeRef t)
    {
        // unwrap nullable
        if (t is TypeRef.Nullable n) return IsAllowedPublicPropertyType(n.Inner);

        if (t is TypeRef.Builtin) return true;
        if (t is TypeRef.Function) return true;
        if (t is TypeRef.WindowOf) return true;

        if (t is TypeRef.Named nt)
        {
            // 允许：trait/enum/window/外部 CTS primitive
            if (nt.ResolvedKind is TypeKind.Trait or TypeKind.Enum or TypeKind.Window) return true;

            if (nt.ResolvedKind == TypeKind.External)
            {
                // 外部仅允许 System.* 基元/常用类型（按你 CTS 映射）
                return IsExternalPrimitiveWhitelist(nt.FullName);
            }

            // class/struct 禁止
            return false;
        }

        // unknown/null/error：默认不允许暴露（更符合防御性）
        return false;
    }

    private static bool IsExternalPrimitiveWhitelist(string fullName)
    {
        return fullName is
            "System.SByte" or "System.Int16" or "System.Int32" or "System.Int64" or
            "System.Byte" or "System.UInt16" or "System.UInt32" or "System.UInt64" or
            "System.Single" or "System.Double" or
            "System.Decimal" or
            "System.Boolean" or
            "System.Char" or
            "System.String" or
            "System.Object";
    }

    private void CheckTraitImplementation(TypeSymbol implType, string traitFullName)
    {
        if (!_index.TryGetType(traitFullName, out var trait) || trait.Kind != TypeKind.Trait)
            return;

        var implDecl = (TypeDeclNode)implType.Decl;
        var ctx = new ResolutionContext(implType.Namespace, GetImports(implType.Namespace));

        foreach (var kv in trait.TraitFunctions)
        {
            foreach (var sig in kv.Value)
            {
                var required = BuildFunctionSignatureKey(sig, ctx, trait.Namespace);
                var matches = FindFunctionsByName(implType, sig.Name.Text);

                var ok = matches.Any(fn => BuildFunctionSignatureKey(fn, ctx, implType.Namespace) == required);
                if (!ok)
                {
                    Emit("AUR4010", DiagnosticSeverity.Error, implDecl.Span,
                        Msg.Diag("AUR4010", traitFullName, sig.Name.Text, required));
                }
            }
        }
    }

    private IEnumerable<FunctionDeclNode> FindFunctionsByName(TypeSymbol type, string name)
        => type.Functions.TryGetValue(name, out var list) ? list : Enumerable.Empty<FunctionDeclNode>();

    private string BuildFunctionSignatureKey(FunctionSignatureNode sig, ResolutionContext ctx, string ownerNs)
    {
        // trait signature ownerNs 传 trait.Namespace
        var p = sig.Parameters.Select(x => x.Type != null ? _typeResolver!.Resolve(x.Type, ctx).ToString() : "var").ToArray();
        var ret = sig.ReturnSpec switch
        {
            null => "void",
            ReturnTypeSpecNode r => _typeResolver!.Resolve(r.ReturnType, ctx).ToString(),
            StateSpecNode s => "state:" + s.StateName.ToString(),
            _ => "void"
        };
        return $"{sig.Name.Text}({string.Join(",", p)}) -> {ret}";
    }

    private string BuildFunctionSignatureKey(FunctionDeclNode fn, ResolutionContext ctx, string ownerNs)
    {
        var p = fn.Parameters.Select(x => x.Type != null ? _typeResolver!.Resolve(x.Type, ctx).ToString() : "var").ToArray();
        var ret = fn.ReturnSpec switch
        {
            null => "void",
            ReturnTypeSpecNode r => _typeResolver!.Resolve(r.ReturnType, ctx).ToString(),
            StateSpecNode s => "state:" + s.StateName.ToString(),
            _ => "void"
        };
        return $"{fn.Name.Text}({string.Join(",", p)}) -> {ret}";
    }

    private void CheckWindowSubset(string ns, WindowDeclNode wd)
    {
        var ctx = new ResolutionContext(ns, GetImports(ns));
        var targetType = _typeResolver!.Resolve(wd.TargetType, ctx);

        if (targetType is not TypeRef.Named n || n.ResolvedKind is not (TypeKind.Class or TypeKind.Struct))
        {
            Emit("AUR4100", DiagnosticSeverity.Error, wd.Span, Msg.Diag("AUR4100.target_type", targetType));
            return;
        }

        if (!_index.TryGetType(n.FullName, out var targetSym))
        {
            Emit("AUR4100", DiagnosticSeverity.Error, wd.Span, Msg.Diag("AUR4100.resolve", n.FullName));
            return;
        }

        var targetCtx = new ResolutionContext(targetSym.Namespace, GetImports(targetSym.Namespace));

        foreach (var m in wd.Members)
        {
            if (!targetSym.Properties.TryGetValue(m.Name.Text, out var prop) || prop.Visibility != Visibility.Public)
            {
                Emit("AUR4100", DiagnosticSeverity.Error, m.Span,
                    Msg.Diag("AUR4100.member", m.Name.Text, n.FullName));
                continue;
            }

            var t1 = _typeResolver.Resolve(prop.Type, targetCtx);
            var t2 = _typeResolver.Resolve(m.Type, ctx);
            if (!TypeEquals(t1, t2))
            {
                Emit("AUR4101", DiagnosticSeverity.Error, m.Span,
                    Msg.Diag("AUR4101", n.FullName, m.Name.Text, t1, t2));
            }
        }
    }

    private static bool TypeEquals(TypeRef a, TypeRef b)
    {
        // 先按结构化比较
        if (Equals(a, b)) return true;

        // Nullable(T) 与 T? 的结构已覆盖。这里做一点宽松：Unknown 不参与判等
        if (a == TypeRef.Unknown || b == TypeRef.Unknown) return true;
        return false;
    }

    private void CheckStateFunctionsInType(TypeSymbol sym, TypeDeclNode td)
    {
        // 只看 type 内的函数成员
        var funcs = sym.Functions.Values.SelectMany(x => x).ToList();
        var stateFuncs = funcs.Where(f => f.ReturnSpec is StateSpecNode).ToList();
        if (stateFuncs.Count == 0) return;

        var ctx = new ResolutionContext(sym.Namespace, GetImports(sym.Namespace));

        var groups = stateFuncs.GroupBy(f => f.Name.Text);
        foreach (var g in groups)
        {
            // 参数签名必须一致
            string? paramSig = null;
            string? stateEnumTypeName = null;
            var seenStates = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fn in g)
            {
                var sig = string.Join(",", fn.Parameters.Select(p => p.Type != null ? _typeResolver!.Resolve(p.Type, ctx).ToString() : "var"));
                paramSig ??= sig;
                if (sig != paramSig)
                    Emit("AUR2230", DiagnosticSeverity.Error, fn.Span, Msg.Diag("AUR2230", g.Key));

                var state = (StateSpecNode)fn.ReturnSpec!;
                var parts = state.StateName.Parts;
                if (parts.Count < 2)
                {
                    Emit("AUR2231", DiagnosticSeverity.Error, fn.Span, Msg.Diag("AUR2231.format", state.StateName));
                    continue;
                }

                var enumType = string.Join(".", parts.Take(parts.Count - 1).Select(x => x.Text));
                var enumValue = parts.Last().Text;

                stateEnumTypeName ??= enumType;
                if (enumType != stateEnumTypeName)
                    Emit("AUR2231", DiagnosticSeverity.Error, fn.Span, Msg.Diag("AUR2231.enum", g.Key));

                if (!seenStates.Add(enumValue))
                    Emit("AUR2232", DiagnosticSeverity.Error, fn.Span, Msg.Diag("AUR2232", enumType, enumValue, g.Key));
            }

            // 覆盖性（推荐：Warning）
            if (stateEnumTypeName != null)
            {
                // 尝试解析枚举
                var enumFull = ResolveTypeNameInNamespace(sym.Namespace, stateEnumTypeName);
                if (enumFull != null && _index.TryGetType(enumFull, out var enumSym) && enumSym.Kind == TypeKind.Enum && enumSym.EnumDecl != null)
                {
                    var all = enumSym.EnumDecl.Members.Select(m => m.Name.Text).ToHashSet(StringComparer.Ordinal);
                    var missing = all.Except(seenStates).ToList();
                    if (missing.Count > 0)
                        Emit("AUR2233", DiagnosticSeverity.Warning, td.Span, Msg.Diag("AUR2233", g.Key, string.Join(", ", missing)));
                }
            }
        }
    }

    /// <summary>
    /// AUR4050: Aura prohibits user-defined constructors. fn new(...) is not allowed.
    /// </summary>
    private void CheckNoUserConstructor(TypeSymbol sym)
    {
        if (sym.Decl is not ClassDeclNode && sym.Decl is not StructDeclNode) return;

        var members = sym.Decl switch
        {
            ClassDeclNode c => c.Members,
            StructDeclNode s => s.Members,
            _ => null
        };
        if (members is null) return;

        foreach (var m in members)
        {
            if (m is FunctionDeclNode fn && fn.Name.Text == "new")
            {
                Emit("AUR4050", DiagnosticSeverity.Error, fn.Span,
                    Msg.Diag("AUR4050", sym.Decl.Name.Text));
            }
        }
    }

    /// <summary>
    /// Validates that if a class defines fn self(...), it follows the Handle/Decode spec:
    /// - Must have exactly one parameter (permission enum type)
    /// - Must return windowof&lt;EnclosingClass&gt;
    /// </summary>
    private void CheckSelfDecodeFunction(TypeSymbol sym)
    {
        if (sym.Decl is not ClassDeclNode && sym.Decl is not StructDeclNode) return;

        var members = sym.Decl switch
        {
            ClassDeclNode c => c.Members,
            StructDeclNode s => s.Members,
            _ => null
        };
        if (members is null) return;

        foreach (var m in members)
        {
            if (m is FunctionDeclNode fn && fn.Name.Text == "self")
            {
                if (fn.Parameters.Count != 1)
                {
                    Emit("AUR4200", DiagnosticSeverity.Error, fn.Span,
                        Msg.Diag("AUR4200", fn.Parameters.Count));
                }

                if (fn.ReturnSpec is ReturnTypeSpecNode rts && rts.ReturnType is not WindowOfTypeNode)
                {
                    Emit("AUR4201", DiagnosticSeverity.Error, fn.Span,
                        Msg.Diag("AUR4201", sym.Decl.Name.Text, rts.ReturnType));
                }
            }
        }
    }

    private string? ResolveTypeNameInNamespace(string ns, string unqualifiedOrQualified)
    {
        if (unqualifiedOrQualified.Contains('.'))
        {
            if (_index.Types.ContainsKey(unqualifiedOrQualified)) return unqualifiedOrQualified;
            // 也可能是相对命名空间
            var cand = TypeResolver.Combine(ns, unqualifiedOrQualified);
            if (_index.Types.ContainsKey(cand)) return cand;
            return null;
        }

        var cand2 = TypeResolver.Combine(ns, unqualifiedOrQualified);
        if (_index.Types.ContainsKey(cand2)) return cand2;
        if (_index.Types.ContainsKey(unqualifiedOrQualified)) return unqualifiedOrQualified;
        return null;
    }

    /* =========================
     * Pass 3: Bodies
     * ========================= */

    private void AnalyzeBodies(CompilationUnitNode cu)
    {
        WalkItemsForBodies(cu.Items, currentNs: "");
    }

    private void WalkItemsForBodies(IReadOnlyList<ICompilationItem> items, string currentNs)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case ImportDeclNode:
                    break;

                case NamespaceDeclNode nd:
                    var nextNs = TypeResolver.Combine(currentNs, nd.Name.ToString());
                    WalkItemsForBodies(nd.Members, nextNs);
                    break;

                case FunctionDeclNode fn:
                    AnalyzeFunction(fn, new ResolutionContext(currentNs, GetImports(currentNs)));
                    break;

                case ClassDeclNode cd:
                    AnalyzeTypeBodies(cd, currentNs);
                    break;

                case StructDeclNode sd:
                    AnalyzeTypeBodies(sd, currentNs);
                    break;

                // trait/enum/window 没有函数体
                default:
                    break;
            }
        }
    }

    private void AnalyzeTypeBodies(TypeDeclNode td, string ns)
    {
        var prevType = _currentTypeName;
        _currentTypeName = td.Name.Text;
        var ctx = new ResolutionContext(ns, GetImports(ns));
        foreach (var m in td.Members)
        {
            if (m is FunctionDeclNode fn)
                AnalyzeFunction(fn, ctx);
        }
        _currentTypeName = prevType;
    }

    private void AnalyzeFunction(FunctionDeclNode fn, ResolutionContext ctx)
    {
        _scope = new LocalScope(parent: null);
        _inAsyncFunction = fn.Modifiers.Contains(FunctionModifier.Async);

        // AUR5002: where constraints are parsed but not yet emitted as CLR generic constraints
        if (fn.WhereClauses.Count > 0)
            Emit("AUR5002", DiagnosticSeverity.Error, fn.WhereClauses[0].Span,
                Msg.Diag("AUR5002", fn.Name.Text));

        // Resolve declared return type for return-statement validation
        _currentReturnType = fn.ReturnSpec switch
        {
            ReturnTypeSpecNode r => _typeResolver!.Resolve(r.ReturnType, ctx),
            StateSpecNode => null,  // state functions don't have a normal return type
            _ => null               // void / unspecified
        };

        // parameters
        foreach (var p in fn.Parameters)
        {
            if (p.Name.Text == "item")
                Emit("AUR4300", DiagnosticSeverity.Error, p.Span, Msg.Diag("AUR4300.param"));

            var t = p.Type != null ? _typeResolver!.Resolve(p.Type, ctx) : TypeRef.Unknown;
            var sym = new LocalSymbol(p.Name.Text, Mutability.Let, t, p.Span);
            if (!_scope.TryDeclare(sym))
                Emit("AUR1020", DiagnosticSeverity.Error, p.Span, Msg.Diag("AUR1020.param", p.Name.Text));
        }

        // body
        switch (fn.Body)
        {
            case FunctionBlockBodyNode bb:
                AnalyzeBlock(bb.Block, ctx);
                break;
            case FunctionExprBodyNode eb:
                AnalyzeExpr(eb.Expr, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                break;
        }
    }

    private void AnalyzeBlock(BlockStmtNode block, ResolutionContext ctx)
    {
        PushScope();
        foreach (var st in block.Statements)
            AnalyzeStmt(st, ctx);
        PopScope();
    }

    private void AnalyzeStmt(StmtNode st, ResolutionContext ctx)
    {
        switch (st)
        {
            case BlockStmtNode b:
                AnalyzeBlock(b, ctx);
                break;

            case VarDeclStmtNode v:
                AnalyzeVarDecl(v, ctx);
                break;

            case ExprStmtNode es:
                AnalyzeExpr(es.Expr, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                break;

            case IfStmtNode iff:
                var ct = AnalyzeExpr(iff.Condition, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                RequireBool(ct, iff.Condition.Span, "if 条件必须是 bool");
                AnalyzeBlock(iff.Then, ctx);
                if (iff.Else != null) AnalyzeStmt(iff.Else, ctx);
                break;

            case WhileStmtNode wh:
                var wt = AnalyzeExpr(wh.Condition, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                RequireBool(wt, wh.Condition.Span, "while 条件必须是 bool");
                AnalyzeBlock(wh.Body, ctx);
                break;

            case ForEachStmtNode fe:
                if (fe.ItemName.Text == "item")
                    Emit("AUR4300", DiagnosticSeverity.Error, fe.ItemName.Span, Msg.Diag("AUR4300.loop"));

                // collection type 暂不强查（可扩展 IEnumerable）
                AnalyzeExpr(fe.Collection, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);

                PushScope();
                // foreach 变量默认 let
                _scope!.TryDeclare(new LocalSymbol(fe.ItemName.Text, Mutability.Let, TypeRef.Unknown, fe.ItemName.Span));
                AnalyzeBlock(fe.Body, ctx);
                PopScope();
                break;

            case ReturnStmtNode ret:
                if (ret.Value != null)
                {
                    var retType = AnalyzeExpr(ret.Value, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                    if (_currentReturnType != null && retType != TypeRef.Unknown && _currentReturnType != TypeRef.Unknown
                        && !IsAssignableTo(retType, _currentReturnType))
                        Emit("AUR2640", DiagnosticSeverity.Error, ret.Span, Msg.Diag("AUR2640", _currentReturnType, retType));
                }
                else if (_currentReturnType != null && _currentReturnType != TypeRef.Unknown
                         && !_currentReturnType.IsVoid)
                {
                    // return without value but function declares a non-void return type — this is an error
                    Emit("AUR2641", DiagnosticSeverity.Error, ret.Span, Msg.Diag("AUR2641", "fn", _currentReturnType));
                }
                break;

            case UsingStmtNode us:
                if (us.Await && !_inAsyncFunction)
                    Emit("AUR2220", DiagnosticSeverity.Error, us.Span, Msg.Diag("AUR2220.await_using"));

                AnalyzeUsingResource(us.Resource, ctx, us.Await);
                if (us.Body != null) AnalyzeBlock(us.Body, ctx);
                break;

            case TryStmtNode ts:
                Emit("AUR5001", DiagnosticSeverity.Warning, ts.Span, Msg.Diag("AUR5001"));
                AnalyzeBlock(ts.TryBlock, ctx);
                foreach (var c in ts.Catches) AnalyzeCatch(c, ctx);
                if (ts.Finally != null) AnalyzeBlock(ts.Finally, ctx);
                break;

            case SwitchStmtNode ss:
                AnalyzeExpr(ss.Value, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                foreach (var sec in ss.Sections) AnalyzeSwitchSection(sec, ctx);
                break;

            default:
                // break/continue/throw 等：throw 里面如果有 expr 也分析
                if (st is ThrowStmtNode th && th.Value != null)
                    AnalyzeExpr(th.Value, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                break;
        }
    }

    private void AnalyzeUsingResource(UsingResourceNode res, ResolutionContext ctx, bool isAwait)
    {
        switch (res)
        {
            case UsingExprResourceNode ue:
                AnalyzeExpr(ue.Expr, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                // IDisposable/IAsyncDisposable 检查：v1 尽力而为（能反射就反射；否则 warning）
                CheckDisposableShape(ue.Expr, ctx, isAwait);
                break;

            case UsingDeclsResourceNode ud:
                PushScope();
                foreach (var d in ud.Decls)
                {
                    if (d.Name.Text == "item")
                        Emit("AUR4300", DiagnosticSeverity.Error, d.Span, Msg.Diag("AUR4300.using"));

                    var initT = AnalyzeExpr(d.Init, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                    var declT = d.Type != null ? _typeResolver!.Resolve(d.Type, ctx) : initT;
                    var sym = new LocalSymbol(d.Name.Text, d.Mutability, declT, d.Span);
                    if (!_scope!.TryDeclare(sym))
                        Emit("AUR1020", DiagnosticSeverity.Error, d.Span, Msg.Diag("AUR1020.var", d.Name.Text));

                    CheckDisposableShape(d.Init, ctx, isAwait);
                }
                PopScope();
                break;
        }
    }

    private void CheckDisposableShape(ExprNode expr, ResolutionContext ctx, bool isAwait)
    {
        // 仅对可以解析成外部 .NET 类型的情况做严格检查，否则提示 warning
        var t = InferExprType(expr, ctx);

        if (t is TypeRef.Named n && n.ResolvedKind == TypeKind.External)
        {
            var dotnet = TryResolveDotNetType(n.FullName);
            if (dotnet != null)
            {
                if (!isAwait)
                {
                    if (!typeof(IDisposable).IsAssignableFrom(dotnet))
                        Emit("AUR2620", DiagnosticSeverity.Error, expr.Span, Msg.Diag("AUR2620", n.FullName));
                }
                else
                {
                    var iAsyncDisp = Type.GetType("System.IAsyncDisposable");
                    if (iAsyncDisp != null && !iAsyncDisp.IsAssignableFrom(dotnet))
                        Emit("AUR2621", DiagnosticSeverity.Error, expr.Span, Msg.Diag("AUR2621", n.FullName));
                }
                return;
            }
        }

        // 无法确定：给出 warning（你可以改成 error）
        Emit("AUR2622", DiagnosticSeverity.Warning, expr.Span, Msg.Diag("AUR2622"));
    }

    private static Type? TryResolveDotNetType(string fullName)
    {
        try
        {
            var t = Type.GetType(fullName);
            if (t != null) return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName);
                if (t != null) return t;
            }
        }
        catch (Exception ex) when (ex is TypeLoadException or System.Reflection.ReflectionTypeLoadException or System.IO.FileNotFoundException or BadImageFormatException)
        {
            // Expected: assembly may contain types we can't load; skip silently.
        }
        return null;
    }

    /// <summary>Walk Aura-defined type's BaseTypes to see if it ultimately derives from Exception.</summary>
    private bool InheritsFromException(TypeSymbol sym, ResolutionContext ctx)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<TypeSymbol>();
        queue.Enqueue(sym);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!visited.Add(cur.FullName)) continue;

            foreach (var bt in cur.BaseTypes)
            {
                var resolved = _typeResolver!.Resolve(bt, ctx);
                if (resolved is not TypeRef.Named n) continue;

                // Check if base is Exception via .NET reflection
                var dotnet = TryResolveDotNetType(n.FullName);
                if (dotnet != null && typeof(Exception).IsAssignableFrom(dotnet))
                    return true;

                // Check known name
                if (n.FullName is "System.Exception" or "Exception")
                    return true;

                // Recurse into Aura-defined base
                if (_index.Types.TryGetValue(n.FullName, out var baseSym))
                    queue.Enqueue(baseSym);
            }
        }
        return false;
    }

    private void AnalyzeCatch(CatchClauseNode c, ResolutionContext ctx)
    {
        PushScope();
        if (c.Name != null)
        {
            var name = c.Name.Text;
            if (name == "item")
                Emit("AUR4300", DiagnosticSeverity.Error, c.Span, Msg.Diag("AUR4300.catch"));

            _scope!.TryDeclare(new LocalSymbol(name, Mutability.Let, TypeRef.Unknown, c.Span));
        }

        if (c.Type != null)
        {
            var t = _typeResolver!.Resolve(c.Type, ctx);
            if (t is TypeRef.Named n)
            {
                // First try .NET reflection-based check
                var dotnetType = TryResolveDotNetType(n.FullName);
                if (dotnetType != null)
                {
                    if (!typeof(Exception).IsAssignableFrom(dotnetType))
                        Emit("AUR2630", DiagnosticSeverity.Error, c.Span, Msg.Diag("AUR2630", n.FullName));
                }
                else if (_index.Types.TryGetValue(n.FullName, out var catchSym))
                {
                    // Aura-defined type: walk BaseTypes looking for Exception
                    if (!InheritsFromException(catchSym, ctx))
                        Emit("AUR2630", DiagnosticSeverity.Error, c.Span, Msg.Diag("AUR2630", n.FullName));
                }
                else
                {
                    // Fallback: name-based heuristic for unresolved types
                    var parts = n.FullName.Split('.');
                    var last = parts.Length > 0 ? parts[^1] : n.FullName;
                    if (last != "Exception" && !last.EndsWith("Exception", StringComparison.Ordinal))
                        Emit("AUR2630", DiagnosticSeverity.Error, c.Span, Msg.Diag("AUR2630", n.FullName));
                }
            }
        }

        AnalyzeBlock(c.Body, ctx);
        PopScope();
    }

    private void AnalyzeSwitchSection(SwitchSectionNode sec, ResolutionContext ctx)
    {
        // v1：如果 pattern 声明了变量，把它们引入 section scope（简化：所有 label 合并，若冲突则报错）
        PushScope();

        var declared = new Dictionary<string, SourceSpan>(StringComparer.Ordinal);
        foreach (var lb in sec.Labels)
        {
            if (lb is CaseLabelNode cl)
            {
                var vars = CollectPatternVariables(cl.Pattern);
                foreach (var v in vars)
                {
                    if (v == "item")
                        Emit("AUR4300", DiagnosticSeverity.Error, cl.Span, Msg.Diag("AUR4300.pattern"));

                    if (declared.ContainsKey(v))
                        continue; // same name from another label: ignore (v1)
                    declared[v] = cl.Span;
                    _scope!.TryDeclare(new LocalSymbol(v, Mutability.Let, TypeRef.Unknown, cl.Span));
                }

                if (cl.WhenGuard != null)
                {
                    var wt = AnalyzeExpr(cl.WhenGuard, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                    RequireBool(wt, cl.WhenGuard.Span, "case when 条件必须是 bool");
                }
            }
        }

        foreach (var st in sec.Statements)
            AnalyzeStmt(st, ctx);

        PopScope();
    }

    private static List<string> CollectPatternVariables(PatternNode pat)
    {
        var list = new List<string>();
        Walk(pat);
        return list;

        void Walk(PatternNode p)
        {
            switch (p)
            {
                case VarPatternNode vp when vp.Name != null:
                    list.Add(vp.Name.Text);
                    break;
                case DeclarationPatternNode dp:
                    list.Add(dp.Name.Text);
                    break;
                case ParenthesizedPatternNode pp:
                    Walk(pp.Inner);
                    break;
                case NotPatternNode np:
                    Walk(np.Inner);
                    break;
                case AndPatternNode ap:
                    Walk(ap.Left); Walk(ap.Right);
                    break;
                case OrPatternNode op:
                    Walk(op.Left); Walk(op.Right);
                    break;
                case PropertyPatternNode pr:
                    foreach (var m in pr.Members) Walk(m.Pattern);
                    break;
                case ListPatternNode lp:
                    foreach (var x in lp.Items) Walk(x);
                    break;
                default:
                    break;
            }
        }
    }

    private void AnalyzeVarDecl(VarDeclStmtNode v, ResolutionContext ctx)
    {
        if (v.Name.Text == "item")
            Emit("AUR4300", DiagnosticSeverity.Error, v.Span, Msg.Diag("AUR4300.var"));

        var initType = v.Init != null ? AnalyzeExpr(v.Init, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1) : TypeRef.Unknown;
        var declaredType = v.Type != null ? _typeResolver!.Resolve(v.Type, ctx) : initType;

        var sym = new LocalSymbol(v.Name.Text, v.Mutability, declaredType, v.Span);
        if (!_scope!.TryDeclare(sym))
            Emit("AUR1020", DiagnosticSeverity.Error, v.Span, Msg.Diag("AUR1020.var", v.Name.Text));

        // let 必须初始化（建议）
        if (v.Mutability == Mutability.Let && v.Init == null)
            Emit("AUR2104", DiagnosticSeverity.Error, v.Span, Msg.Diag("AUR2104"));
    }

    /* =========================
     * Expressions / inference
     * ========================= */

    private TypeRef AnalyzeExpr(ExprNode expr, ResolutionContext ctx, bool allowPlaceholder, bool inPredicateIndex, int pipeStageIndex)
    {
        // 先做结构检查 + 递归
        switch (expr)
        {
            case InterpolatedStringExprNode:
                return new TypeRef.Builtin(BuiltinTypeKind.String);

            case LiteralExprNode lit:
                return InferLiteralType(lit);

            case NameExprNode name:
                // item 仅允许出现在谓词索引器 index 表达式内
                if (name.Name.Text == "item" && !inPredicateIndex)
                    Emit("AUR4300", DiagnosticSeverity.Error, name.Span, Msg.Diag("AUR4300.name"));
                return LookupNameType(name.Name.Text);

            case AssignmentExprNode ass:
                // let 赋值检查
                if (ass.Left is NameExprNode ln && _scope != null && _scope.TryLookup(ln.Name.Text, out var local))
                {
                    if (local.Mutability == Mutability.Let)
                        Emit("AUR2101", DiagnosticSeverity.Error, ass.Span, Msg.Diag("AUR2101", ln.Name.Text));
                }

                // ??= 左侧可空检查（尽力而为）
                if (ass.Op == "??=")
                {
                    var lt = InferExprType(ass.Left, ctx);
                    if (!IsNullableType(lt))
                        Emit("AUR2302", DiagnosticSeverity.Error, ass.Left.Span, Msg.Diag("AUR2302", lt));
                }

                AnalyzeExpr(ass.Left, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                return AnalyzeExpr(ass.Right, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);

            case ConditionalExprNode ce:
                var c0 = AnalyzeExpr(ce.Condition, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                RequireBool(c0, ce.Condition.Span, "条件表达式需要 bool");
                var t1 = AnalyzeExpr(ce.Then, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                var t2 = AnalyzeExpr(ce.Else, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                var ct = CommonType(t1, t2);
                if (ct == TypeRef.Unknown)
                    Emit("AUR2321", DiagnosticSeverity.Error, ce.Span, Msg.Diag("AUR2321", t1, t2));
                return ct;

            case BinaryExprNode bin:
                var l = AnalyzeExpr(bin.Left, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                var r = AnalyzeExpr(bin.Right, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                return InferBinary(bin.Op, l, r, bin.Span);

            case UnaryExprNode un:
                if (un.Op == "await" && !_inAsyncFunction)
                    Emit("AUR2220", DiagnosticSeverity.Error, un.Span, Msg.Diag("AUR2220.await"));
                return AnalyzeExpr(un.Operand, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);

            case CallExprNode call:
                foreach (var a in call.Args)
                {
                    AnalyzeArgument(a, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                }
                var calleeType = AnalyzeExpr(call.Callee, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                // If callee resolved to a known type (e.g., from MemberAccess), use it;
                // otherwise try symbol lookup for simple name calls
                if (calleeType != TypeRef.Unknown) return calleeType;
                return TryResolveCallReturnType(call, ctx);

            case MemberAccessExprNode ma:
                var targetType = AnalyzeExpr(ma.Target, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                return TryResolveMemberType(targetType, ma.Member.Text, ctx);

            case IndexExprNode ix:
                AnalyzeExpr(ix.Target, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                // index 表达式允许 item
                AnalyzeExpr(ix.Index, ctx, allowPlaceholder, inPredicateIndex: true, pipeStageIndex);
                return TypeRef.Unknown;

            case PipeExprNode pipe:
                // stage0 禁止占位符；后续允许占位符
                for (int i = 0; i < pipe.Stages.Count; i++)
                {
                    var allow = i > 0;
                    AnalyzeExpr(pipe.Stages[i], ctx, allowPlaceholder: allow, inPredicateIndex: false, pipeStageIndex: i);
                }
                return TypeRef.Unknown;

            case GuardExprNode ge:
                // expr
                var baseT = AnalyzeExpr(ge.Expr, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);

                // handlers: 要求 lambda 且 1 参数（尽力而为）
                foreach (var h in ge.Handlers)
                {
                    if (h is LambdaExprNode lam)
                    {
                        if (lam.Parameters.Count != 1)
                            Emit("AUR2410", DiagnosticSeverity.Error, h.Span, Msg.Diag("AUR2410.error"));
                    }
                    else
                    {
                        Emit("AUR2410", DiagnosticSeverity.Warning, h.Span, Msg.Diag("AUR2410.warn"));
                    }

                    AnalyzeExpr(h, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                }
                return baseT;

            case LambdaExprNode lam2:
                PushScope();
                foreach (var p in lam2.Parameters)
                {
                    if (p.Name.Text == "item")
                        Emit("AUR4300", DiagnosticSeverity.Error, p.Span, Msg.Diag("AUR4300.lambda"));

                    var pt = p.Type != null ? _typeResolver!.Resolve(p.Type, ctx) : TypeRef.Unknown;
                    if (!_scope!.TryDeclare(new LocalSymbol(p.Name.Text, Mutability.Let, pt, p.Span)))
                        Emit("AUR1020", DiagnosticSeverity.Error, p.Span, Msg.Diag("AUR1020.lambda", p.Name.Text));
                }
                var bodyT = AnalyzeExpr(lam2.Body, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                PopScope();
                return new TypeRef.Function(lam2.Parameters.Select(p => p.Type != null ? _typeResolver!.Resolve(p.Type, ctx) : TypeRef.Unknown).ToList(), bodyT);

            case SwitchExprNode sw:
                var switchValType = AnalyzeExpr(sw.Value, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);

                TypeRef? acc = null;
                var hasDiscard = false;
                foreach (var arm in sw.Arms)
                {
                    PushScope();
                    foreach (var v in CollectPatternVariables(arm.Pattern))
                        _scope!.TryDeclare(new LocalSymbol(v, Mutability.Let, TypeRef.Unknown, arm.Span));

                    if (arm.Pattern is DiscardPatternNode) hasDiscard = true;

                    if (arm.WhenGuard != null)
                    {
                        var wtt = AnalyzeExpr(arm.WhenGuard, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                        RequireBool(wtt, arm.WhenGuard.Span, "switch arm when 条件必须是 bool");
                    }

                    var rt = AnalyzeExpr(arm.Result, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                    acc = acc == null ? rt : CommonType(acc, rt);
                    PopScope();
                }

                if (!hasDiscard)
                {
                    // Check if switch value is an enum and all members are covered
                    var enumExhaustive = false;
                    if (switchValType is TypeRef.Named enumRef && enumRef.ResolvedKind == TypeKind.Enum
                        && _index.Types.TryGetValue(enumRef.FullName, out var enumSym) && enumSym.EnumDecl != null)
                    {
                        var memberNames = new HashSet<string>(enumSym.EnumDecl.Members.Select(m => m.Name.Text), StringComparer.Ordinal);
                        foreach (var arm in sw.Arms)
                        {
                            if (arm.Pattern is ConstantPatternNode { Value: ConstNameNode cn })
                            {
                                // Last part is the member name (e.g., "Idle" from "State.Idle")
                                var memberName = cn.Name.Parts[^1].Text;
                                memberNames.Remove(memberName);
                            }
                        }
                        enumExhaustive = memberNames.Count == 0;
                    }

                    if (!enumExhaustive)
                        Emit("AUR2511", DiagnosticSeverity.Error, sw.Span, Msg.Diag("AUR2511"));
                }

                if (acc == null) return TypeRef.Unknown;
                if (acc == TypeRef.Unknown)
                    Emit("AUR2510", DiagnosticSeverity.Warning, sw.Span, Msg.Diag("AUR2510"));
                return acc;

            case NewExprNode ne:
                // Resolve the target type
                var nt = _typeResolver!.Resolve(ne.TypeRef, ctx);
                var targetName = ne.TypeRef is NamedTypeNode ntn ? ntn.Name.ToString() : null;
                if (nt is TypeRef.Named ntt)
                {
                    if (ntt.ResolvedKind is TypeKind.Trait or TypeKind.Enum or TypeKind.Window)
                        Emit("AUR4031", DiagnosticSeverity.Error, ne.Span, Msg.Diag("AUR4031.kind", ntt.ResolvedKind, ntt.FullName));
                }

                // ── Builder constraints ──────────────────────────────────────
                //
                // 1. VoidBuilder is the ONLY type that can be new() with zero args
                //    (the bootstrap for the entire builder system).
                // 2. CLR external types: new forbidden — must use builder chain.
                // 3. Aura-defined types: new with named args (property init) allowed.
                //    Zero-arg new A() is sugar for new A(VoidBuilder()).

                bool isVoidBuilder = targetName == "VoidBuilder";

                // Rule 1: zero-arg new() only for VoidBuilder
                if (ne.Args.Count == 0 && !isVoidBuilder)
                    Emit("AUR4031", DiagnosticSeverity.Error, ne.Span, Msg.Diag("AUR4031.noarg"));

                // Rule 2: CLR external types — new forbidden (use builder chain)
                if (nt is TypeRef.Named ntt2 && ntt2.ResolvedKind == TypeKind.External && !isVoidBuilder)
                    Emit("AUR4032", DiagnosticSeverity.Error, ne.Span, Msg.Diag("AUR4032"));

                foreach (var a in ne.Args)
                    AnalyzeArgument(a, ctx, allowPlaceholder, inPredicateIndex, pipeStageIndex);
                return nt;

            case BuilderNewExprNode bne:
                // new(builder) — builder-based instantiation: always allowed
                AnalyzeExpr(bne.Builder, ctx, allowPlaceholder: false, inPredicateIndex: false, pipeStageIndex: -1);
                // Result type is object (runtime-determined by the builder)
                return new TypeRef.Named("object", TypeKind.External);

            default:
                // 其他表达式：尽量递归其子表达式
                return InferExprType(expr, ctx);
        }
    }

    private void AnalyzeArgument(ArgumentNode arg, ResolutionContext ctx, bool allowPlaceholder, bool inPredicateIndex, int pipeStageIndex)
    {
        switch (arg)
        {
            case PlaceholderArgNode ph:
                if (!allowPlaceholder || pipeStageIndex <= 0)
                    Emit("AUR2402", DiagnosticSeverity.Error, ph.Span, Msg.Diag("AUR2402"));
                break;

            case PositionalArgNode pa:
                AnalyzeExpr(pa.Value, ctx, allowPlaceholder: false, inPredicateIndex, pipeStageIndex);
                break;

            case NamedArgNode na:
                AnalyzeExpr(na.Value, ctx, allowPlaceholder: false, inPredicateIndex, pipeStageIndex);
                break;
        }
    }

    private TypeRef InferExprType(ExprNode expr, ResolutionContext ctx)
    {
        // 仅用于无法递归时的兜底推断
        return expr switch
        {
            LiteralExprNode lit => InferLiteralType(lit),
            InterpolatedStringExprNode => new TypeRef.Builtin(BuiltinTypeKind.String),
            NameExprNode n => LookupNameType(n.Name.Text),
            NewExprNode ne => _typeResolver!.Resolve(ne.TypeRef, ctx),
            _ => TypeRef.Unknown
        };
    }

    private TypeRef LookupNameType(string name)
    {
        if (_scope != null && _scope.TryLookup(name, out var sym))
            return sym.DeclaredType;

        return TypeRef.Unknown;
    }

    private static TypeRef InferLiteralType(LiteralExprNode lit)
    {
        return lit.Kind switch
        {
            LiteralKind.Null => TypeRef.Null,
            LiteralKind.True or LiteralKind.False => new TypeRef.Builtin(BuiltinTypeKind.Bool),
            LiteralKind.Int => new TypeRef.Builtin(BuiltinTypeKind.I32),
            LiteralKind.Float => new TypeRef.Builtin(BuiltinTypeKind.F64),
            LiteralKind.String => new TypeRef.Builtin(BuiltinTypeKind.String),
            LiteralKind.Char => new TypeRef.Builtin(BuiltinTypeKind.Char),
            _ => TypeRef.Unknown
        };
    }

    private TypeRef InferBinary(string op, TypeRef left, TypeRef right, SourceSpan span)
    {
        // 禁止位运算补强（即使语法层没有，也防御性）
        if (op is "&" or "|" or "^" or "<<" or ">>")
        {
            Emit("AUR4400", DiagnosticSeverity.Error, span, Msg.Diag("AUR4400", op));
            return TypeRef.Error;
        }

        if (op is "&&" or "||")
        {
            RequireBool(left, span, "逻辑运算需要 bool");
            RequireBool(right, span, "逻辑运算需要 bool");
            return new TypeRef.Builtin(BuiltinTypeKind.Bool);
        }

        if (op is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            // v1：只要求两边能推到同一种数值/或未知
            return new TypeRef.Builtin(BuiltinTypeKind.Bool);
        }

        if (op is "+" or "-" or "*" or "/" or "%")
        {
            if (left is TypeRef.Builtin bl && right is TypeRef.Builtin br)
            {
                // string + string → string (concatenation)
                if (op == "+" && (bl.Kind == BuiltinTypeKind.String || br.Kind == BuiltinTypeKind.String))
                    return new TypeRef.Builtin(BuiltinTypeKind.String);

                // char + char → string (concatenation)
                if (op == "+" && bl.Kind == BuiltinTypeKind.Char && br.Kind == BuiltinTypeKind.Char)
                    return new TypeRef.Builtin(BuiltinTypeKind.String);

                // 浮点优先
                if (bl.Kind is BuiltinTypeKind.F64 or BuiltinTypeKind.F32 ||
                    br.Kind is BuiltinTypeKind.F64 or BuiltinTypeKind.F32)
                    return new TypeRef.Builtin(BuiltinTypeKind.F64);

                // decimal
                if (bl.Kind == BuiltinTypeKind.Decimal || br.Kind == BuiltinTypeKind.Decimal)
                    return new TypeRef.Builtin(BuiltinTypeKind.Decimal);

                // 其余整数当 i32
                return new TypeRef.Builtin(BuiltinTypeKind.I32);
            }
            return TypeRef.Unknown;
        }

        if (op == "??")
        {
            if (!IsNullableType(left))
                Emit("AUR2310", DiagnosticSeverity.Error, span, Msg.Diag("AUR2310", left));
            return CommonType(UnwrapNullable(left), right);
        }

        return TypeRef.Unknown;
    }

    private static bool IsNullableType(TypeRef t)
        => t is TypeRef.Nullable || t == TypeRef.Null;

    private static TypeRef UnwrapNullable(TypeRef t)
        => t is TypeRef.Nullable n ? n.Inner : t;

    private TypeRef CommonType(TypeRef a, TypeRef b)
    {
        if (a == TypeRef.Error || b == TypeRef.Error) return TypeRef.Error;
        if (Equals(a, b)) return a;

        if (a == TypeRef.Null && b is TypeRef.Nullable) return b;
        if (b == TypeRef.Null && a is TypeRef.Nullable) return a;

        // 数值提升：i32 + i64 -> i64 等（v1 仅做最常用几种）
        if (a is TypeRef.Builtin ba && b is TypeRef.Builtin bb)
        {
            if (ba.Kind == BuiltinTypeKind.F64 || bb.Kind == BuiltinTypeKind.F64)
                return new TypeRef.Builtin(BuiltinTypeKind.F64);

            if (ba.Kind == BuiltinTypeKind.I64 || bb.Kind == BuiltinTypeKind.I64)
                return new TypeRef.Builtin(BuiltinTypeKind.I64);

            if (ba.Kind == BuiltinTypeKind.I32 && bb.Kind == BuiltinTypeKind.I32)
                return new TypeRef.Builtin(BuiltinTypeKind.I32);
        }

        // null 与某非空：无法合并（需要目标类型）
        if (a == TypeRef.Null || b == TypeRef.Null) return TypeRef.Unknown;

        return TypeRef.Unknown;
    }

    /// <summary>v1 assignability check: exact match, numeric widening, nullable, and object target.</summary>
    private bool IsAssignableTo(TypeRef source, TypeRef target)
    {
        if (Equals(source, target)) return true;
        if (target == TypeRef.Unknown || source == TypeRef.Unknown) return true;

        // null assignable to nullable or reference types (class, trait, external, string)
        if (source == TypeRef.Null && target is TypeRef.Nullable) return true;
        if (source == TypeRef.Null && target is TypeRef.Named { ResolvedKind: TypeKind.Class or TypeKind.Trait or TypeKind.External }) return true;
        if (source == TypeRef.Null && target is TypeRef.Builtin { Kind: BuiltinTypeKind.String or BuiltinTypeKind.Object }) return true;

        // T assignable to T?
        if (target is TypeRef.Nullable nt && Equals(source, nt.Inner)) return true;

        // numeric widening: i32 -> i64, i32 -> f64, i64 -> f64, f32 -> f64
        if (source is TypeRef.Builtin sb && target is TypeRef.Builtin tb)
        {
            return (sb.Kind, tb.Kind) switch
            {
                (BuiltinTypeKind.I32, BuiltinTypeKind.I64) => true,
                (BuiltinTypeKind.I32, BuiltinTypeKind.F64) => true,
                (BuiltinTypeKind.I64, BuiltinTypeKind.F64) => true,
                (BuiltinTypeKind.F32, BuiltinTypeKind.F64) => true,
                _ => false
            };
        }

        // anything assignable to object
        if (target is TypeRef.Named { FullName: "object" or "System.Object" }) return true;

        return false;
    }

    private void RequireBool(TypeRef t, SourceSpan span, string message)
    {
        if (t is TypeRef.Builtin b && b.Kind == BuiltinTypeKind.Bool) return;
        if (t == TypeRef.Unknown) return; // v1：不强报
        Emit("AUR2601", DiagnosticSeverity.Error, span, Msg.Diag("AUR2601", message, t));
    }

    /// <summary>Try to resolve the return type of a simple name call (foo()) by looking up in SymbolIndex.</summary>
    private TypeRef TryResolveCallReturnType(CallExprNode call, ResolutionContext ctx)
    {
        if (call.Callee is not NameExprNode nameExpr) return TypeRef.Unknown;

        var funcName = nameExpr.Name.Text;

        // Look in the current type first
        if (_currentTypeName != null && _index.Types.TryGetValue(_currentTypeName, out var typeSym)
            && typeSym.Functions.TryGetValue(funcName, out var fns) && fns.Count > 0)
            return ResolveFunctionReturnType(fns[0], ctx);

        // Look in all types
        foreach (var sym in _index.Types.Values)
        {
            if (sym.Functions.TryGetValue(funcName, out var fns2) && fns2.Count > 0)
                return ResolveFunctionReturnType(fns2[0], ctx);
        }

        return TypeRef.Unknown;
    }

    private TypeRef ResolveFunctionReturnType(FunctionDeclNode fn, ResolutionContext ctx)
    {
        return fn.ReturnSpec switch
        {
            ReturnTypeSpecNode r => _typeResolver!.Resolve(r.ReturnType, ctx),
            _ => TypeRef.Unknown
        };
    }

    /// <summary>Try to resolve the type of a member (property or single-return function) on a known type.</summary>
    private TypeRef TryResolveMemberType(TypeRef targetType, string memberName, ResolutionContext ctx)
    {
        if (targetType is not TypeRef.Named tn) return TypeRef.Unknown;
        if (!_index.Types.TryGetValue(tn.FullName, out var sym)) return TypeRef.Unknown;

        // Check properties first
        if (sym.Properties.TryGetValue(memberName, out var prop))
            return _typeResolver!.Resolve(prop.Type, ctx);

        // Check fields
        if (sym.Fields.TryGetValue(memberName, out var field) && field.Type != null)
            return _typeResolver!.Resolve(field.Type, ctx);

        // Check functions (return a function type or the return type for later call resolution)
        if (sym.Functions.TryGetValue(memberName, out var fns) && fns.Count > 0)
            return ResolveFunctionReturnType(fns[0], ctx);

        return TypeRef.Unknown;
    }

    /* =========================
     * scope helpers
     * ========================= */

    private void PushScope() => _scope = new LocalScope(_scope);

    private void PopScope() => _scope = _scope?.Parent;

    /* =========================
     * diag helper
     * ========================= */

    private void Emit(string code, DiagnosticSeverity sev, SourceSpan span, string msg)
        => _diags.Add(new SemanticDiagnostic(code, sev, span, msg));
}
