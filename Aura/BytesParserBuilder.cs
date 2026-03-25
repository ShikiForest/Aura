namespace Aura;

/// <summary>
/// Arg builder for <see cref="BytesParserBuilder{T}"/>.
/// Carries the inner <see cref="ICaster{string, T}"/> that BytesParser wraps.
/// </summary>
/// <example>
/// <code>
/// class MyBytesParserArgs : BytesParserArgs&lt;MyData&gt; {
///     // inherits caster property
/// }
/// let xmlParser = new(xmlParserBuilder)
/// let args = new MyBytesParserArgs(caster: xmlParser)
/// let builder = new BytesParserBuilder&lt;MyData&gt;(args: args)
/// let bytesParser = new(builder)
/// </code>
/// </example>
public class BytesParserArgs<T> : CLRConstructorArgBuilder
{
    /// <summary>
    /// The inner ICaster&lt;string, T&gt; that BytesParser delegates to.
    /// </summary>
    public ICaster<string, T>? Caster { get; set; }
}

/// <summary>
/// Builder for <see cref="BytesParser{T}"/>.
/// Requires a <see cref="BytesParserArgs{T}"/> with the inner caster.
/// <code>
/// let xmlParser = new(xmlParserBuilder)
/// let args = new BytesParserArgs&lt;MyData&gt;(caster: xmlParser)
/// let builder = new BytesParserBuilder&lt;MyData&gt;(args: args)
/// let bytesParser = new(builder)
/// let data = bytes.morph(bytesParser)
/// </code>
/// </summary>
public class BytesParserBuilder<T> : IBuilder<BytesParser<T>>
{
    private readonly CLRConstructorArgBuilder? _argBuilder;

    public BytesParserBuilder()
    {
    }

    public BytesParserBuilder(CLRConstructorArgBuilder argBuilder)
    {
        _argBuilder = argBuilder;
    }

    public Dictionary<string, object> GetConstructorDictionary()
    {
        return _argBuilder?.GetConstructorDictionary() ?? new Dictionary<string, object>();
    }

    public BytesParser<T> Build(Dictionary<string, object> args)
    {
        ICaster<string, T>? inner = null;

        // Extract from arg builder directly if available
        if (_argBuilder is BytesParserArgs<T> typedArgs)
            inner = typedArgs.Caster;

        // Fallback: try from args dictionary
        if (inner is null && args.TryGetValue("Caster", out var casterObj) && casterObj is ICaster<string, T> c)
            inner = c;

        if (inner is null)
            throw new InvalidOperationException("BytesParserBuilder requires an ICaster<string, T> via BytesParserArgs.");

        return new BytesParser<T>(inner);
    }
}
