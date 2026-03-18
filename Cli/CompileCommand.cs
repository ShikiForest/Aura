using System.Diagnostics;
using AuraLang.Ast;
using AuraLang.CodeGen;
using AuraLang.Lowering;
using AuraLang.Semantics;

namespace AntlrCompiler.Cli;

internal static class CompileCommand
{
    // ── Public result ────────────────────────────────────────────────────────

    /// <summary>
    /// Returned by <see cref="ExecuteCore"/> so that <see cref="RunCommand"/>
    /// can inspect results without re-running the compile phase.
    /// </summary>
    internal sealed record CompileResult(
        bool     Success,
        string?  DllPath,
        int      TotalErrors,
        int      TotalWarnings,
        TimeSpan TotalTime
    );

    // ── Entry point (top-level subcommand) ───────────────────────────────────

    public static int Execute(CompileOptions opts)
    {
        var result = ExecuteCore(opts);
        ConsoleWriter.Summary(result.TotalErrors, result.TotalWarnings, result.TotalTime);
        return result.Success ? 0 : 1;
    }

    // ── Core pipeline (reused by RunCommand) ─────────────────────────────────

    internal static CompileResult ExecuteCore(CompileOptions opts)
    {
        var totalSw = Stopwatch.StartNew();
        int errors = 0, warnings = 0;

        // ── Load source ───────────────────────────────────────────────────────
        if (!File.Exists(opts.SourceFile))
        {
            ConsoleWriter.Error($"File not found: {opts.SourceFile}");
            return Fail(1, 0, totalSw);
        }

        var source      = File.ReadAllText(opts.SourceFile);
        var sourceLines = source.Split('\n');
        var name        = opts.AssemblyName
                          ?? Path.GetFileNameWithoutExtension(opts.SourceFile);
        var outputPath  = opts.OutputPath
                          ?? Path.Combine(
                              Path.GetDirectoryName(Path.GetFullPath(opts.SourceFile))!,
                              name + ".dll");

        Console.WriteLine($"  source : {opts.SourceFile}");
        Console.WriteLine($"  output : {outputPath}");
        ConsoleWriter.Verbose(opts.Verbose, $"assembly name : {name}");

        // ── Step 1: Parse ─────────────────────────────────────────────────────
        ConsoleWriter.PhaseHeader(1, "Parsing");
        var sw = Stopwatch.StartNew();
        var parseResult = AuraFrontEnd.ParseCompilationUnit(source);
        sw.Stop();

        foreach (var d in parseResult.Diagnostics)
        {
            DiagnosticFormatter.Emit(DiagnosticFormatter.FromParse(d),
                opts.SourceFile, sourceLines, opts.Verbose);
            errors++;
        }

        if (errors > 0 || parseResult.Ast is null)
        {
            ConsoleWriter.PhaseFail("Parse failed", sw.Elapsed);
            return Fail(errors, warnings, totalSw);
        }

        ConsoleWriter.PhaseOk("AST built", sw.Elapsed);
        var ast = parseResult.Ast;

        // ── Step 2: Semantic analysis ─────────────────────────────────────────
        ConsoleWriter.PhaseHeader(2, "Semantic analysis");
        sw = Stopwatch.StartNew();
        var semResult = SemanticFrontEnd.Check(ast);
        sw.Stop();

        int semErrors = 0;
        foreach (var d in semResult.Diagnostics)
        {
            var fd = DiagnosticFormatter.FromSemantic(d);
            DiagnosticFormatter.Emit(fd, opts.SourceFile, sourceLines, opts.Verbose);
            if (fd.Level == DiagLevel.Error)   semErrors++;
            else if (fd.Level == DiagLevel.Warning) warnings++;
        }
        errors += semErrors;

        if (semErrors > 0)
        {
            ConsoleWriter.PhaseFail("Semantic analysis failed", sw.Elapsed);
            return Fail(errors, warnings, totalSw);
        }

        ConsoleWriter.PhaseOk($"{semResult.Diagnostics.Count} diagnostic(s)", sw.Elapsed);

        // ── Step 3: Lowering ──────────────────────────────────────────────────
        CompilationUnitNode loweredAst;

        if (opts.NoLower)
        {
            Console.WriteLine();
            ConsoleWriter.Verbose(true, "[3] Lowering skipped (--no-lower)");
            loweredAst = ast;
        }
        else
        {
            ConsoleWriter.PhaseHeader(3, "Lowering");
            sw = Stopwatch.StartNew();
            var lowerer      = new AuraLowerer();
            var lowerResult  = lowerer.Lower(ast);
            sw.Stop();

            int lowerErrors = 0;
            foreach (var d in lowerResult.Diagnostics)
            {
                var fd = DiagnosticFormatter.FromLowering(d);
                DiagnosticFormatter.Emit(fd, opts.SourceFile, sourceLines, opts.Verbose);
                if (fd.Level == DiagLevel.Error)   lowerErrors++;
                else if (fd.Level == DiagLevel.Warning) warnings++;
            }
            errors += lowerErrors;

            if (lowerErrors > 0)
            {
                ConsoleWriter.PhaseFail("Lowering failed", sw.Elapsed);
                return Fail(errors, warnings, totalSw);
            }

            loweredAst = lowerResult.Ast;
            ConsoleWriter.PhaseOk($"{lowerResult.Diagnostics.Count} diagnostic(s)", sw.Elapsed);
        }

        // ── Step 4: Code generation ───────────────────────────────────────────
        ConsoleWriter.PhaseHeader(4, "Code generation");
        sw = Stopwatch.StartNew();

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var codegen       = new AuraCecilCodeGenerator();
        var codegenResult = codegen.Generate(loweredAst, outputPath, name,
            sourceFilePath: Path.GetFullPath(opts.SourceFile));
        sw.Stop();

        int codegenErrors = 0;
        foreach (var d in codegenResult.Diagnostics)
        {
            var fd = DiagnosticFormatter.FromCodeGen(d);
            DiagnosticFormatter.Emit(fd, opts.SourceFile, sourceLines, opts.Verbose);
            if (fd.Level == DiagLevel.Error)   codegenErrors++;
            else if (fd.Level == DiagLevel.Warning) warnings++;
        }
        errors += codegenErrors;

        if (codegenErrors > 0 || !File.Exists(outputPath))
        {
            ConsoleWriter.PhaseFail("Code generation failed", sw.Elapsed);
            return Fail(errors, warnings, totalSw);
        }

        var dllSize = new FileInfo(outputPath).Length;
        var pdbPath = Path.ChangeExtension(outputPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            var pdbSize = new FileInfo(pdbPath).Length;
            ConsoleWriter.PhaseOk(
                $"DLL written: {outputPath}  ({dllSize:N0} bytes)", sw.Elapsed);
            ConsoleWriter.Verbose(true,
                $"PDB written: {pdbPath}  ({pdbSize:N0} bytes)");
        }
        else
        {
            ConsoleWriter.PhaseOk(
                $"DLL written: {outputPath}  ({dllSize:N0} bytes)", sw.Elapsed);
        }

        totalSw.Stop();
        return new CompileResult(true, outputPath, errors, warnings, totalSw.Elapsed);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CompileResult Fail(int errors, int warnings, Stopwatch sw)
    {
        sw.Stop();
        return new CompileResult(false, null, errors, warnings, sw.Elapsed);
    }
}
