using System.Reflection;

namespace Aura;

/// <summary>
/// Abstract base class for building CLR external type constructor arguments.
/// Designed for inheritance: subclasses define properties that map to constructor parameters.
///
/// <para>
/// <see cref="GetConstructorDictionary"/> scans the subclass's public instance properties
/// (excluding <see cref="Args"/> itself) and populates the <see cref="Args"/> dictionary.
/// </para>
///
/// <para>Usage:</para>
/// <code>
/// class MyFormArgs : CLRConstructorArgBuilder {
///     property text: string
///     property width: int
/// }
///
/// let args = new MyFormArgs(text: "Hello", width: 400)
/// let builder = new CLRExternalTypeBuilder&lt;Form&gt;(args: args)
/// let form = new(builder)
/// </code>
/// </summary>
public abstract class CLRConstructorArgBuilder
{
    /// <summary>
    /// The accumulated constructor arguments. Populated by <see cref="GetConstructorDictionary"/>.
    /// </summary>
    public Dictionary<string, object> Args { get; } = new();

    /// <summary>
    /// Scans this subclass's public instance properties (excluding Args) and
    /// populates <see cref="Args"/> with their current values.
    /// </summary>
    public virtual Dictionary<string, object> GetConstructorDictionary()
    {
        Args.Clear();
        var props = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in props)
        {
            if (prop.Name == "Args" || !prop.CanRead)
                continue;
            var val = prop.GetValue(this);
            if (val is not null)
                Args[prop.Name] = val;
        }
        return Args;
    }
}
