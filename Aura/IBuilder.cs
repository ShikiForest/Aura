namespace Aura;

/// <summary>
/// Base builder interface for Aura's builder-based instantiation pattern.
/// All object creation in Aura goes through builders: <c>new(builder)</c>.
/// </summary>
/// <typeparam name="T">The type to be constructed.</typeparam>
public interface IBuilder<T>
{
    /// <summary>
    /// Returns a dictionary of constructor parameter names to their values.
    /// </summary>
    Dictionary<string, object> GetConstructorDictionary();

    /// <summary>
    /// Builds an instance of <typeparamref name="T"/> using the provided dictionary.
    /// </summary>
    T Build(Dictionary<string, object> args);
}
