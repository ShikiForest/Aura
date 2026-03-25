namespace Aura;

/// <summary>
/// Arg builder for <see cref="XmlCasterBuilder{T}"/>.
/// XmlCaster requires no arguments, so this is an empty arg builder.
/// </summary>
public class XmlCasterArgs : CLRConstructorArgBuilder
{
}

/// <summary>
/// Builder for <see cref="XmlCaster{T}"/>.
/// <code>
/// let args = new XmlCasterArgs()
/// let builder = new XmlCasterBuilder&lt;MyData&gt;(args: args)
/// let caster = new(builder)
/// let xml = data.morph(caster)
/// </code>
/// </summary>
public class XmlCasterBuilder<T> : IBuilder<XmlCaster<T>>
{
    private readonly CLRConstructorArgBuilder? _argBuilder;

    public XmlCasterBuilder()
    {
    }

    public XmlCasterBuilder(CLRConstructorArgBuilder argBuilder)
    {
        _argBuilder = argBuilder;
    }

    public Dictionary<string, object> GetConstructorDictionary()
    {
        return _argBuilder?.GetConstructorDictionary() ?? new Dictionary<string, object>();
    }

    public XmlCaster<T> Build(Dictionary<string, object> args)
    {
        return new XmlCaster<T>();
    }
}
