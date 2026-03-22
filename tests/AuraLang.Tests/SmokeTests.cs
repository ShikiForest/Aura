using System.Diagnostics;
using Xunit;

namespace AuraLang.Tests;

/// <summary>
/// End-to-end smoke tests: compile .aura samples and verify they
/// produce expected output when run.
/// </summary>
public class SmokeTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string CompilerDll = Path.Combine(ProjectRoot, "bin", "Debug", "net10.0", "AuraLang.dll");

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "AuraLang.csproj")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find project root (AuraLang.csproj)");
    }

    private static (int ExitCode, string Output) RunCompiler(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{Path.Combine(ProjectRoot, "AuraLang.csproj")}\" -- {string.Join(' ', args)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["AURA_LANG"] = "en";
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        return (proc.ExitCode, stdout + stderr);
    }

    [Fact]
    public void TestSuite_CompileAndRun_AllPass()
    {
        var samplePath = Path.Combine(ProjectRoot, "samples", "test_suite.aura");
        if (!File.Exists(samplePath))
        {
            // Skip if sample not available
            return;
        }

        var (exitCode, output) = RunCompiler("run", samplePath);
        // The test suite prints PASS/FAIL for each test
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS", output);
    }

    [Fact]
    public void HardTest_CompileAndRun_AllPass()
    {
        var samplePath = Path.Combine(ProjectRoot, "samples", "hard_test.aura");
        if (!File.Exists(samplePath))
            return;

        var (exitCode, output) = RunCompiler("run", samplePath);
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS", output);
    }

    [Fact]
    public void Experiment_Compiles()
    {
        var samplePath = Path.Combine(ProjectRoot, "samples", "experiment.aura");
        if (!File.Exists(samplePath))
            return;

        var (exitCode, output) = RunCompiler("compile", samplePath);
        Assert.Contains("0 error", output);
    }
}
