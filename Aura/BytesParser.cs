using System.Text.Json;

namespace Aura;

/// <summary>
/// Deserializes a UTF-8 byte array (JSON binary) into an object of type <typeparamref name="T"/>.
/// Implements ICaster&lt;byte[], T&gt;.
/// </summary>
/// <example>
/// <code>
/// let parser = new BytesParser&lt;MyData&gt;()
/// let data = bytes.morph(parser)   // byte[] -> MyData
/// </code>
/// </example>
public class BytesParser<T> : ICaster<byte[], T>
{
    public T Cast(byte[] obj)
    {
        return JsonSerializer.Deserialize<T>(obj)!;
    }
}
