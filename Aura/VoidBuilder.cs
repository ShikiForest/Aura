namespace Aura;

/// <summary>
/// The bootstrap builder. VoidBuilder is the ONLY type in Aura that can be
/// created with <c>new VoidBuilder()</c> (zero-arg new).
/// It represents "no constructor arguments needed" — a default/empty instantiation.
///
/// <para>Usage:</para>
/// <code>
/// let a = new A(new VoidBuilder())   // equivalent to A() in other languages
/// let a = new A()                     // sugar — compiler rewrites to above
/// </code>
/// </summary>
public sealed class VoidBuilder : IBuilder<object>
{
    public Dictionary<string, object> GetConstructorDictionary()
    {
        return new Dictionary<string, object>();
    }

    public object Build(Dictionary<string, object> args)
    {
        // VoidBuilder is a sentinel — the actual construction is handled
        // by the caller (compiler emits newobj for the target type).
        return null!;
    }
}
