using System.Text.Json;

namespace Aura;

/// <summary>
/// Serializes an object of type <typeparamref name="T"/> to a UTF-8 byte array (JSON binary).
/// Implements ICaster&lt;T, byte[]&gt;.
/// </summary>
/// <example>
/// <code>
/// let caster = new BytesCaster&lt;MyData&gt;()
/// let bytes = data.morph(caster)   // MyData -> byte[]
/// </code>
/// </example>
public class BytesCaster<T> : ICaster<T, byte[]>
{
    public byte[] Cast(T obj)
    {
        return JsonSerializer.SerializeToUtf8Bytes(obj);
    }
}
