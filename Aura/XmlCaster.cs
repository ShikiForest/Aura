using System.Xml.Serialization;

namespace Aura;

/// <summary>
/// Serializes an object of type <typeparamref name="T"/> to an XML string.
/// Implements ICaster&lt;T, string&gt;.
/// </summary>
/// <example>
/// <code>
/// let caster = new XmlCaster&lt;MyData&gt;()
/// let xml = data.morph(caster)   // MyData -> string (XML)
/// </code>
/// </example>
public class XmlCaster<T> : ICaster<T, string>
{
    public string Cast(T obj)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var sw = new StringWriter();
        serializer.Serialize(sw, obj);
        return sw.ToString();
    }
}
