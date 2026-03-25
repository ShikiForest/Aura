using System.Text;

namespace Aura;

/// <summary>
/// Adapter that bridges byte[] → string → T.
/// Converts UTF-8 bytes to string, then delegates to an inner <see cref="ICaster{string, T}"/>.
/// </summary>
/// <typeparam name="T">The target type.</typeparam>
/// <example>
/// <code>
/// // Compose with any string parser:
/// let xmlParser = new XmlParser&lt;MyData&gt;()
/// let bytesParser = new BytesParser&lt;MyData&gt;(caster: xmlParser)
/// let data = bytes.morph(bytesParser)   // byte[] -> string -> MyData
/// </code>
/// </example>
public class BytesParser<T> : ICaster<byte[], T>
{
    private readonly ICaster<string, T> _inner;

    public BytesParser(ICaster<string, T> caster)
    {
        _inner = caster;
    }

    public T Cast(byte[] obj)
    {
        var str = Encoding.UTF8.GetString(obj);
        return _inner.Cast(str);
    }
}
