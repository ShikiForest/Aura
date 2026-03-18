using AuraLang.Ast;

namespace AuraLang.Semantics;

/// <summary>把 AST 的 TypeNode 解析成语义 TypeRef（只解析本 compilation unit 内类型 + 内建类型）。</summary>
public sealed class TypeResolver
{
    private readonly SymbolIndex _index;

    public TypeResolver(SymbolIndex index) => _index = index;

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

        // 如果是带命名空间的全限定名，直接查
        if (name.Contains('.'))
        {
            if (_index.TryGetType(name, out var sym))
                return new TypeRef.Named(name, sym.Kind);
            return new TypeRef.Named(name, TypeKind.External);
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

        // 外部类型（比如 System.String / System.Console 的未全限定写法如果靠 import System，这里也会拼出来）
        foreach (var imp in ctx.Imports)
        {
            var ext = Combine(imp, name);
            // 如果 import 的是 System，ext 可能是 System.Console 等
            return new TypeRef.Named(ext, TypeKind.External);
        }

        return new TypeRef.Named(name, TypeKind.External);
    }

    public static string Combine(string ns, string name)
        => string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}

public readonly record struct ResolutionContext(string Namespace, IReadOnlyList<string> Imports);
