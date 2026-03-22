using System.Reflection;

namespace Aura;

/// <summary>
/// Builder for CLR external types. Uses reflection to construct instances.
/// This is the only type in Aura that is allowed to use normal <c>new</c>.
/// </summary>
/// <typeparam name="T">The CLR type to construct.</typeparam>
public class CLRExternalTypeBuilder<T> : IBuilder<T>
{
    public Dictionary<string, object> GetConstructorDictionary()
    {
        return new Dictionary<string, object>();
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
