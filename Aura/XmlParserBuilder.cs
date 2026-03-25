namespace Aura;

/// <summary>
/// Arg builder for <see cref="XmlParserBuilder{T}"/>.
/// XmlParser requires no arguments, so this is an empty arg builder.
/// </summary>
public class XmlParserArgs : CLRConstructorArgBuilder
{
}

/// <summary>
/// Builder for <see cref="XmlParser{T}"/>.
/// <code>
/// let args = new XmlParserArgs()
/// let builder = new XmlParserBuilder&lt;MyData&gt;(args: args)
/// let parser = new(builder)
/// let data = xml.morph(parser)
/// </code>
/// </summary>
public class XmlParserBuilder<T> : IBuilder<XmlParser<T>>
{
    private readonly CLRConstructorArgBuilder? _argBuilder;

    public XmlParserBuilder()
    {
    }

    public XmlParserBuilder(CLRConstructorArgBuilder argBuilder)
    {
        _argBuilder = argBuilder;
    }

    public Dictionary<string, object> GetConstructorDictionary()
    {
        return _argBuilder?.GetConstructorDictionary() ?? new Dictionary<string, object>();
    }

    public XmlParser<T> Build(Dictionary<string, object> args)
    {
        return new XmlParser<T>();
    }
}
