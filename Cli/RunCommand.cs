using System.Diagnostics;
using AuraLang.CodeGen.Tools;
using AuraLang.I18n;

namespace AuraLang.Cli;

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
            opts.NoLower,
            opts.Lang);

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
        ConsoleWriter.PhaseHeader(5, Msg.Cli("phase_packaging"));
        var sw = Stopwatch.StartNew();

        string hostDir;
        try
        {
            hostDir = AuraExePackager.CreateHostProject(
                dllPath, outDir, hostName, packagerOpts);
        }
        catch (Exception ex)
        {
            ConsoleWriter.PhaseFail(Msg.Cli("host_project_failed", ex.Message), sw.Elapsed);
            ConsoleWriter.Summary(
                compileResult.TotalErrors + 1,
                compileResult.TotalWarnings,
                compileResult.TotalTime + sw.Elapsed);
            return 1;
        }

        ConsoleWriter.Verbose(opts.Verbose, Msg.Cli("label_host_project", hostDir));

        // ── 6: dotnet publish ─────────────────────────────────────────────────
        ConsoleWriter.PhaseHeader(6, Msg.Cli("phase_publishing"));
        var pubOut   = Path.Combine(outDir, hostName, "publish");
        int pubExit  = AuraExePackager.PublishHostProject(hostDir, pubOut, packagerOpts);
        sw.Stop();

        if (pubExit != 0)
        {
            ConsoleWriter.PhaseFail(Msg.Cli("publish_failed", pubExit), sw.Elapsed);
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
            ConsoleWriter.Error(Msg.Cli("exe_not_found", exePath));
            return 1;
        }

        Console.WriteLine($"\n  {Msg.Cli("running", Path.GetFileName(exePath))}");
        Console.WriteLine(new string('─', 60));

        // Pass stdio directly to the terminal — do NOT redirect.
        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            UseShellExecute  = false,
            CreateNoWindow   = false,
        };

        var proc = Process.Start(psi);
        if (proc is null)
        {
            ConsoleWriter.Error(Msg.Cli("process_start_failed", exePath));
            return 1;
        }
        using var _ = proc;
        proc.WaitForExit();

        Console.WriteLine(new string('─', 60));
        ConsoleWriter.Verbose(opts.Verbose, Msg.Cli("process_exited", proc.ExitCode));

        return proc.ExitCode;
    }
}
