using System.Text;

namespace Aura;

/// <summary>
/// Adapter that bridges T → string → byte[].
/// Wraps an inner <see cref="ICaster{T, string}"/> and converts the resulting string to UTF-8 bytes.
/// </summary>
/// <typeparam name="T">The source type.</typeparam>
/// <example>
/// <code>
/// // Compose with any string caster:
/// let xmlCaster = new XmlCaster&lt;MyData&gt;()
/// let bytesCaster = new BytesCaster&lt;MyData&gt;(caster: xmlCaster)
/// let bytes = data.morph(bytesCaster)   // MyData -> XML string -> byte[]
/// </code>
/// </example>
public class BytesCaster<T> : ICaster<T, byte[]>
{
    private readonly ICaster<T, string> _inner;

    public BytesCaster(ICaster<T, string> caster)
    {
        _inner = caster;
    }

    public byte[] Cast(T obj)
    {
        var str = _inner.Cast(obj);
        return Encoding.UTF8.GetBytes(str);
    }
}
