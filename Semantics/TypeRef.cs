using System.Text;
using AuraLang.Ast;

namespace AuraLang.Semantics;

/// <summary>语义层类型表示。尽量与 Aura 源级类型一致，而不是直接等价于 .NET Type。</summary>
public abstract record TypeRef
{
    public static readonly TypeRef Unknown = new UnknownTypeRef();
    public static readonly TypeRef Error = new ErrorTypeRef();
    public static readonly TypeRef Null = new NullTypeRef();

    public virtual bool IsNullable => false;
    public virtual bool IsVoid => false;

    public sealed record UnknownTypeRef : TypeRef;
    public sealed record ErrorTypeRef : TypeRef;
    public sealed record NullTypeRef : TypeRef;

    public sealed record Builtin(BuiltinTypeKind Kind) : TypeRef
    {
        public override bool IsVoid => Kind == BuiltinTypeKind.Void;
        public override string ToString() => Kind.ToString();
    }

    public sealed record Named(string FullName, TypeKind? ResolvedKind = null) : TypeRef
    {
        public override string ToString() => FullName;
    }

    public sealed record Nullable(TypeRef Inner) : TypeRef
    {
        public override bool IsNullable => true;
        public override string ToString() => $"{Inner}?";
    }

    public sealed record Function(IReadOnlyList<TypeRef> Parameters, TypeRef Return) : TypeRef
    {
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("(");
            for (int i = 0; i < Parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Parameters[i]);
            }
            sb.Append(") -> ");
            sb.Append(Return);
            return sb.ToString();
        }
    }

    public sealed record WindowOf(TypeRef Inner) : TypeRef
    {
        public override string ToString() => $"windowof<{Inner}>";
    }
}

/// <summary>用户/内部类型的种类（用于架构约束与解析）。</summary>
public enum TypeKind
{
    Class,
    Struct,
    Trait,
    Enum,
    Window,
    External
}
