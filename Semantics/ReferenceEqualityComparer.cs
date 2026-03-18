using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AuraLang.Semantics;

/// <summary>用于把 AST 节点作为 Dictionary key（按引用相等）。</summary>
public sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
