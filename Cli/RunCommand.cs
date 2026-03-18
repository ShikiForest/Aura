using System.Diagnostics;
using AuraLang.CodeGen.Tools;

namespace AntlrCompiler.Cli;

/// <summary>
/// Implements `aura run`: compile → package → execute.
/// </summary>
internal static class RunCommand
{
    public static int Execute(RunOptions opts)
    {
        // ── 1-4: Compile ─────────────────────────────────────────────────────
        var compileOpts = new CompileOptions(
            opts.SourceFile,
            opts.OutputPath,
            opts.AssemblyName,
            opts.Verbose,
            opts.NoLower);

        var compileResult = CompileCommand.ExecuteCore(compileOpts);

        if (!compileResult.Success)
        {
            ConsoleWriter.Summary(
                compileResult.TotalErrors,
                compileResult.TotalWarnings,
                compileResult.TotalTime);
            return 1;
        }

        var dllPath    = compileResult.DllPath!;
        var name       = opts.AssemblyName
                         ?? Path.GetFileNameWithoutExtension(opts.SourceFile);
        var hostName   = name + "Host";
        var outDir     = Path.GetDirectoryName(Path.GetFullPath(dllPath))!;

        var packagerOpts = new AuraExePackager.PackagerOptions(
            TargetFramework: opts.TargetFramework,
            SelfContained:   opts.SelfContained);

        // ── 5: Create host project ────────────────────────────────────────────
        ConsoleWriter.PhaseHeader(5, "EXE packaging");
        var sw = Stopwatch.StartNew();

        string hostDir;
        try
        {
            hostDir = AuraExePackager.CreateHostProject(
                dllPath, outDir, hostName, packagerOpts);
        }
        catch (Exception ex)
        {
            ConsoleWriter.PhaseFail($"Failed to create host project: {ex.Message}", sw.Elapsed);
            ConsoleWriter.Summary(
                compileResult.TotalErrors + 1,
                compileResult.TotalWarnings,
                compileResult.TotalTime + sw.Elapsed);
            return 1;
        }

        ConsoleWriter.Verbose(opts.Verbose, $"Host project: {hostDir}");

        // ── 6: dotnet publish ─────────────────────────────────────────────────
        ConsoleWriter.PhaseHeader(6, "Publishing");
        var pubOut   = Path.Combine(outDir, hostName, "publish");
        int pubExit  = AuraExePackager.PublishHostProject(hostDir, pubOut, packagerOpts);
        sw.Stop();

        if (pubExit != 0)
        {
            ConsoleWriter.PhaseFail($"dotnet publish exited with code {pubExit}", sw.Elapsed);
            ConsoleWriter.Summary(
                compileResult.TotalErrors + 1,
                compileResult.TotalWarnings,
                compileResult.TotalTime + sw.Elapsed);
            return 1;
        }

        // Locate the published executable (Windows: .exe, Linux/macOS: no extension)
        var exePath = Path.Combine(pubOut, hostName + ".exe");
        if (!File.Exists(exePath))
            exePath = Path.Combine(pubOut, hostName);

        ConsoleWriter.PhaseOk($"EXE: {exePath}", sw.Elapsed);

        // ── Summary (compile + package) ───────────────────────────────────────
        ConsoleWriter.Summary(
            compileResult.TotalErrors,
            compileResult.TotalWarnings,
            compileResult.TotalTime + sw.Elapsed);

        // ── 7: Execute ────────────────────────────────────────────────────────
        if (!File.Exists(exePath))
        {
            ConsoleWriter.Error($"EXE not found after publish: {exePath}");
            return 1;
        }

        Console.WriteLine($"\n  Running: {Path.GetFileName(exePath)}");
        Console.WriteLine(new string('─', 60));

        // Pass stdio directly to the terminal — do NOT redirect.
        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            UseShellExecute  = false,
            CreateNoWindow   = false,
        };

        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        Console.WriteLine(new string('─', 60));
        ConsoleWriter.Verbose(opts.Verbose, $"Process exited with code {proc.ExitCode}");

        return proc.ExitCode;
    }
}
