using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

internal static class Program
{
    private static int Main(string[] args)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "hello.dll");
        if (!File.Exists(dllPath))
        {
            Console.Error.WriteLine("Cannot find generated Aura DLL next to the executable: " + dllPath);
            return 1;
        }

        var asm = Assembly.LoadFrom(dllPath);
        var type = asm.GetType("AuraModule");
        if (type is null)
        {
            Console.Error.WriteLine("Entry type not found: AuraModule");
            return 2;
        }

        var method = type.GetMethod("main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {
            Console.Error.WriteLine("Entry method not found: AuraModule.main (static)");
            return 3;
        }

        object? result;
        try
        {
            result = method.Invoke(null, Array.Empty<object?>());
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            Console.Error.WriteLine(tie.InnerException.ToString());
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }

        if (result is Task t)
        {
            // It's fine to wait here: this is the program entrypoint.
            t.GetAwaiter().GetResult();
        }

        return 0;
    }
}
