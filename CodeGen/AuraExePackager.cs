using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AuraLang.CodeGen.Tools;

/// <summary>
/// Utility for turning a generated Aura DLL into a runnable EXE by generating
/// a tiny .NET host project (and optionally invoking `dotnet publish`).
///
/// This works by copying the generated DLL next to the EXE and using reflection
/// to invoke an entry method (defaults to `AuraModule.main`).
/// </summary>
public static class AuraExePackager
{
    public sealed record PackagerOptions(
        string TargetFramework = "net10.0",
        string Configuration = "Release",
        string? RuntimeIdentifier = null,
        bool SelfContained = false,
        string EntryTypeFullName = "AuraModule",
        string EntryMethodName = "main"
    );

    /// <summary>
    /// Creates a host project at:
    ///   {outputDir}/{hostProjectName}/
    ///
    /// It will copy the <paramref name="generatedDllPath"/> next to the host executable at build/publish time.
    /// </summary>
    public static string CreateHostProject(
        string generatedDllPath,
        string outputDir,
        string hostProjectName = "AuraHost",
        PackagerOptions? options = null)
    {
        options ??= new PackagerOptions();

        if (string.IsNullOrWhiteSpace(generatedDllPath))
            throw new ArgumentException("generatedDllPath is required.", nameof(generatedDllPath));

        if (!File.Exists(generatedDllPath))
            throw new FileNotFoundException("Generated DLL not found.", generatedDllPath);

        Directory.CreateDirectory(outputDir);

        var hostDir = Path.Combine(outputDir, hostProjectName);
        Directory.CreateDirectory(hostDir);

        var dllFileName = Path.GetFileName(generatedDllPath);

        // Program.cs
        var programPath = Path.Combine(hostDir, "Program.cs");
        File.WriteAllText(programPath, GenerateProgramCs(dllFileName, options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // csproj
        var csprojPath = Path.Combine(hostDir, hostProjectName + ".csproj");
        File.WriteAllText(csprojPath, GenerateCsproj(generatedDllPath, dllFileName, options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return hostDir;
    }

    /// <summary>
    /// Runs `dotnet publish` on the created host project.
    /// </summary>
    public static int PublishHostProject(
        string hostProjectDir,
        string? outputDir = null,
        PackagerOptions? options = null)
    {
        options ??= new PackagerOptions();

        if (!Directory.Exists(hostProjectDir))
            throw new DirectoryNotFoundException(hostProjectDir);

        var args = new StringBuilder();
        args.Append("publish");
        args.Append(" -c ").Append(options.Configuration);
        args.Append(" -f ").Append(options.TargetFramework);

        if (!string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
            args.Append(" -r ").Append(options.RuntimeIdentifier);

        args.Append(" --self-contained ").Append(options.SelfContained ? "true" : "false");

        if (!string.IsNullOrWhiteSpace(outputDir))
            args.Append(" -o ").Append(Quote(outputDir!));

        return RunProcess("dotnet", args.ToString(), hostProjectDir);
    }

    private static string GenerateCsproj(string generatedDllPath, string dllFileName, PackagerOptions options)
    {
        // Copy the generated DLL into the host output directory at build/publish time.
        // Use <Link> to control the destination file name (keep it stable).
        var dllPathEscaped = generatedDllPath
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

        return $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>{options.TargetFramework}</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <None Include=""{dllPathEscaped}"">
      <Link>{dllFileName}</Link>
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
";
    }

    private static string GenerateProgramCs(string dllFileName, PackagerOptions options)
    {
        // Reflection-based entry invocation so we don't need to reference the generated assembly at compile time.
        // We also support async entrypoints by waiting if the return value is a Task.
        var entryType = options.EntryTypeFullName.Replace("\"", "\\\"");
        var entryMethod = options.EntryMethodName.Replace("\"", "\\\"");

        return $@"using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

internal static class Program
{{
    private static int Main(string[] args)
    {{
        var dllPath = Path.Combine(AppContext.BaseDirectory, ""{dllFileName}"");
        if (!File.Exists(dllPath))
        {{
            Console.Error.WriteLine(""Cannot find generated Aura DLL next to the executable: "" + dllPath);
            return 1;
        }}

        var asm = Assembly.LoadFrom(dllPath);
        var type = asm.GetType(""{entryType}"");
        if (type is null)
        {{
            Console.Error.WriteLine(""Entry type not found: {entryType}"");
            return 2;
        }}

        var method = type.GetMethod(""{entryMethod}"", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {{
            Console.Error.WriteLine(""Entry method not found: {entryType}.{entryMethod} (static)"");
            return 3;
        }}

        object? result;
        try
        {{
            result = method.Invoke(null, Array.Empty<object?>());
        }}
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {{
            Console.Error.WriteLine(tie.InnerException.ToString());
            return 4;
        }}
        catch (Exception ex)
        {{
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }}

        // Propagate exit codes from user code
        if (result is Task<int> taskInt)
        {{
            return taskInt.GetAwaiter().GetResult();
        }}
        if (result is Task t)
        {{
            t.GetAwaiter().GetResult();
            return 0;
        }}
        if (result is int exitCode)
        {{
            return exitCode;
        }}

        return 0;
    }}
}}
";
    }

    private static int RunProcess(string fileName, string arguments, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = new Process { StartInfo = psi };

        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();

        return p.ExitCode;
    }

    private static string Quote(string s)
        => s.Contains(' ') ? "\"" + s + "\"" : s;
}
