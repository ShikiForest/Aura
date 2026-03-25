using System.Xml.Serialization;

namespace Aura;

/// <summary>
/// Deserializes an XML string into an object of type <typeparamref name="T"/>.
/// Implements ICaster&lt;string, T&gt;.
/// </summary>
/// <example>
/// <code>
/// let parser = new XmlParser&lt;MyData&gt;()
/// let data = xml.morph(parser)   // string (XML) -> MyData
/// </code>
/// </example>
public class XmlParser<T> : ICaster<string, T>
{
    public T Cast(string obj)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var sr = new StringReader(obj);
        return (T)serializer.Deserialize(sr)!;
    }
}
