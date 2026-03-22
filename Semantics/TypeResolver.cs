using AuraLang.Ast;
using AuraLang.I18n;

namespace AuraLang.Semantics;

/// <summary>把 AST 的 TypeNode 解析成语义 TypeRef（只解析本 compilation unit 内类型 + 内建类型）。</summary>
public sealed class TypeResolver
{
    private readonly SymbolIndex _index;
    private readonly List<SemanticDiagnostic> _diags;

    public TypeResolver(SymbolIndex index, List<SemanticDiagnostic> diags)
    {
        _index = index;
        _diags = diags;
    }

    public TypeRef Resolve(TypeNode node, ResolutionContext ctx)
    {
        return node switch
        {
            BuiltinTypeNode b => new TypeRef.Builtin(b.Kind),
            NullableTypeNode n => new TypeRef.Nullable(Resolve(n.Inner, ctx)),
            FunctionTypeNode f => new TypeRef.Function(f.ParamTypes.Select(p => Resolve(p, ctx)).ToList(), Resolve(f.ReturnType, ctx)),
            WindowOfTypeNode w => new TypeRef.WindowOf(Resolve(w.Inner, ctx)),
            NamedTypeNode nt => ResolveNamed(nt, ctx),
            _ => TypeRef.Unknown
        };
    }

    public TypeRef ResolveTypeReference(TypeNode node, ResolutionContext ctx) => Resolve(node, ctx);

    private TypeRef ResolveNamed(NamedTypeNode nt, ResolutionContext ctx)
    {
        var name = nt.Name.ToString();

        // Warn if generic type arguments are provided but not yet resolved
        if (nt.TypeArgs.Count > 0)
        {
            _diags.Add(new SemanticDiagnostic(
                "AUR5003", DiagnosticSeverity.Warning, nt.Span,
                Msg.Diag("AUR5003", name, nt.TypeArgs.Count)));
        }

        // Fully-qualified name: check Aura index first, then CLR
        if (name.Contains('.'))
        {
            if (_index.TryGetType(name, out var sym))
                return new TypeRef.Named(name, sym.Kind);
            if (TryResolveDotNetType(name) is not null)
                return new TypeRef.Named(name, TypeKind.External);
            return TypeRef.Unknown;
        }

        // 1) 当前命名空间
        var cand = Combine(ctx.Namespace, name);
        if (_index.TryGetType(cand, out var sym1))
            return new TypeRef.Named(sym1.FullName, sym1.Kind);

        // 2) 全局
        if (_index.TryGetType(name, out var sym2))
            return new TypeRef.Named(sym2.FullName, sym2.Kind);

        // 3) imports
        foreach (var imp in ctx.Imports)
        {
            var cand2 = Combine(imp, name);
            if (_index.TryGetType(cand2, out var sym3))
                return new TypeRef.Named(sym3.FullName, sym3.Kind);
        }

        // External type: try each import + name combination, but only accept if CLR can actually resolve it
        foreach (var imp in ctx.Imports)
        {
            var ext = Combine(imp, name);
            if (TryResolveDotNetType(ext) is not null)
                return new TypeRef.Named(ext, TypeKind.External);
        }

        // Last resort: try bare name as external (e.g., fully-qualified name used without import)
        if (TryResolveDotNetType(name) is not null)
            return new TypeRef.Named(name, TypeKind.External);

        // Unresolved: return Unknown so diagnostics can catch it
        return TypeRef.Unknown;
    }

    public static string Combine(string ns, string name)
        => string.IsNullOrEmpty(ns) ? name : ns + "." + name;

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
        }
        return null;
    }
}

public readonly record struct ResolutionContext(string Namespace, IReadOnlyList<string> Imports);
