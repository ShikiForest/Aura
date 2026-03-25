namespace Aura;

/// <summary>
/// Trait for type conversion in Aura.
/// Replaces ad-hoc .serialize() / .deserialize() with a composable, type-safe pattern.
/// </summary>
/// <typeparam name="TIn">The source type to cast from.</typeparam>
/// <typeparam name="TOut">The target type to cast to.</typeparam>
/// <example>
/// <code>
/// // Aura usage:
/// class JsonCaster : ICaster&lt;MyData, string&gt; {
///     fn cast(obj: MyData) -> string {
///         // serialize MyData to JSON string
///     }
/// }
///
/// let data = new MyData(name: "test")
/// let json = data.morph(new JsonCaster())   // -> string
///
/// // Reverse direction:
/// class JsonParser : ICaster&lt;string, MyData&gt; {
///     fn cast(obj: string) -> MyData {
///         // deserialize JSON string to MyData
///     }
/// }
///
/// let parsed = json.morph(new JsonParser())  // -> MyData
/// </code>
/// </example>
public interface ICaster<TIn, TOut>
{
    /// <summary>
    /// Casts (converts/transforms) the input object to the output type.
    /// </summary>
    TOut Cast(TIn obj);
}

/// <summary>
/// Extension method host for the morph operation.
/// morph&lt;TIn, TOut&gt;(caster) calls caster.Cast(this) and returns TOut.
/// </summary>
public static class CasterExtensions
{
    /// <summary>
    /// Applies a caster to transform this object into a different type.
    /// This is the Aura extension method: obj.morph(caster) -> TOut
    /// </summary>
    public static TOut Morph<TIn, TOut>(this TIn obj, ICaster<TIn, TOut> caster)
    {
        return caster.Cast(obj);
    }
}
