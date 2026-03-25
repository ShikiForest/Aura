namespace Aura;

/// <summary>
/// Arg builder for <see cref="BytesCasterBuilder{T}"/>.
/// Carries the inner <see cref="ICaster{T, string}"/> that BytesCaster wraps.
/// </summary>
/// <example>
/// <code>
/// class MyBytesCasterArgs : BytesCasterArgs&lt;MyData&gt; {
///     // inherits caster property
/// }
/// let xmlCaster = new(xmlCasterBuilder)
/// let args = new MyBytesCasterArgs(caster: xmlCaster)
/// let builder = new BytesCasterBuilder&lt;MyData&gt;(args: args)
/// let bytesCaster = new(builder)
/// </code>
/// </example>
public class BytesCasterArgs<T> : CLRConstructorArgBuilder
{
    /// <summary>
    /// The inner ICaster&lt;T, string&gt; that BytesCaster delegates to.
    /// </summary>
    public ICaster<T, string>? Caster { get; set; }
}

/// <summary>
/// Builder for <see cref="BytesCaster{T}"/>.
/// Requires a <see cref="BytesCasterArgs{T}"/> with the inner caster.
/// <code>
/// let xmlCaster = new(xmlCasterBuilder)
/// let args = new BytesCasterArgs&lt;MyData&gt;(caster: xmlCaster)
/// let builder = new BytesCasterBuilder&lt;MyData&gt;(args: args)
/// let bytesCaster = new(builder)
/// let bytes = data.morph(bytesCaster)
/// </code>
/// </summary>
public class BytesCasterBuilder<T> : IBuilder<BytesCaster<T>>
{
    private readonly CLRConstructorArgBuilder? _argBuilder;

    public BytesCasterBuilder()
    {
    }

    public BytesCasterBuilder(CLRConstructorArgBuilder argBuilder)
    {
        _argBuilder = argBuilder;
    }

    public Dictionary<string, object> GetConstructorDictionary()
    {
        return _argBuilder?.GetConstructorDictionary() ?? new Dictionary<string, object>();
    }

    public BytesCaster<T> Build(Dictionary<string, object> args)
    {
        ICaster<T, string>? inner = null;

        // Extract from arg builder directly if available
        if (_argBuilder is BytesCasterArgs<T> typedArgs)
            inner = typedArgs.Caster;

        // Fallback: try from args dictionary
        if (inner is null && args.TryGetValue("Caster", out var casterObj) && casterObj is ICaster<T, string> c)
            inner = c;

        if (inner is null)
            throw new InvalidOperationException("BytesCasterBuilder requires an ICaster<T, string> via BytesCasterArgs.");

        return new BytesCaster<T>(inner);
    }
}
