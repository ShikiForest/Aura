using System.Reflection;

namespace Aura;

/// <summary>
/// Builder for CLR external types. Accepts a <see cref="CLRConstructorArgBuilder"/>
/// to define constructor arguments, then uses reflection to construct instances.
///
/// <para>Chain: CLRConstructorArgBuilder → CLRExternalTypeBuilder&lt;T&gt; → T</para>
///
/// <code>
/// class MyFormArgs : CLRConstructorArgBuilder {
///     property text: string
/// }
/// let args = new MyFormArgs(text: "Hello")
/// let builder = new CLRExternalTypeBuilder&lt;Form&gt;(args: args)
/// let form = new(builder)
/// </code>
/// </summary>
/// <typeparam name="T">The CLR type to construct.</typeparam>
public class CLRExternalTypeBuilder<T> : IBuilder<T>
{
    private readonly CLRConstructorArgBuilder? _argBuilder;

    public CLRExternalTypeBuilder()
    {
    }

    public CLRExternalTypeBuilder(CLRConstructorArgBuilder argBuilder)
    {
        _argBuilder = argBuilder;
    }

    public Dictionary<string, object> GetConstructorDictionary()
    {
        return _argBuilder?.GetConstructorDictionary() ?? new Dictionary<string, object>();
    }

    public T Build(Dictionary<string, object> args)
    {
        if (args.Count == 0)
            return Activator.CreateInstance<T>();

        var ctors = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        foreach (var ctor in ctors)
        {
            var parameters = ctor.GetParameters();
            if (parameters.Length == args.Count)
            {
                var ctorArgs = new object[parameters.Length];
                bool match = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (args.TryGetValue(parameters[i].Name!, out var val))
                        ctorArgs[i] = val;
                    else
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return (T)ctor.Invoke(ctorArgs);
            }
        }

        // Fallback: positional
        return (T)Activator.CreateInstance(typeof(T), args.Values.ToArray())!;
    }
}
