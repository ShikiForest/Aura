using System.Diagnostics;
using AuraLang.Ast;
using AuraLang.CodeGen;
using AuraLang.I18n;
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
            ConsoleWriter.Error(Msg.Cli("file_not_found", opts.SourceFile));
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

        Console.WriteLine($"  {Msg.Cli("label_source")} : {opts.SourceFile}");
        Console.WriteLine($"  {Msg.Cli("label_output")} : {outputPath}");
        ConsoleWriter.Verbose(opts.Verbose, $"{Msg.Cli("label_assembly")} : {name}");

        // ── Step 1: Parse ─────────────────────────────────────────────────────
        ConsoleWriter.PhaseHeader(1, Msg.Cli("phase_parsing"));
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
            ConsoleWriter.PhaseFail(Msg.Cli("parse_failed"), sw.Elapsed);
            return Fail(errors, warnings, totalSw);
        }

        ConsoleWriter.PhaseOk(Msg.Cli("ast_built"), sw.Elapsed);
        var ast = parseResult.Ast;

        // ── Step 2: Semantic analysis ─────────────────────────────────────────
        ConsoleWriter.PhaseHeader(2, Msg.Cli("phase_semantic"));
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
            ConsoleWriter.PhaseFail(Msg.Cli("semantic_failed"), sw.Elapsed);
            return Fail(errors, warnings, totalSw);
        }

        ConsoleWriter.PhaseOk(Msg.Cli("n_diagnostics", semResult.Diagnostics.Count), sw.Elapsed);

        // ── Step 3: Lowering ──────────────────────────────────────────────────
        CompilationUnitNode loweredAst;

        if (opts.NoLower)
        {
            Console.WriteLine();
            ConsoleWriter.Verbose(true, "[3] " + Msg.Cli("lowering_skipped"));
            loweredAst = ast;
        }
        else
        {
            ConsoleWriter.PhaseHeader(3, Msg.Cli("phase_lowering"));
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
                ConsoleWriter.PhaseFail(Msg.Cli("lowering_failed"), sw.Elapsed);
                return Fail(errors, warnings, totalSw);
            }

            loweredAst = lowerResult.Ast;
            ConsoleWriter.PhaseOk(Msg.Cli("n_diagnostics", lowerResult.Diagnostics.Count), sw.Elapsed);
        }

        // ── Step 4: Code generation ───────────────────────────────────────────
        ConsoleWriter.PhaseHeader(4, Msg.Cli("phase_codegen"));
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
            ConsoleWriter.PhaseFail(Msg.Cli("codegen_failed"), sw.Elapsed);
            return Fail(errors, warnings, totalSw);
        }

        var dllSize = new FileInfo(outputPath).Length;
        var pdbPath = Path.ChangeExtension(outputPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            var pdbSize = new FileInfo(pdbPath).Length;
            ConsoleWriter.PhaseOk(
                Msg.Cli("dll_written", outputPath, $"{dllSize:N0}"), sw.Elapsed);
            ConsoleWriter.Verbose(true,
                Msg.Cli("pdb_written", pdbPath, $"{pdbSize:N0}"));
        }
        else
        {
            ConsoleWriter.PhaseOk(
                Msg.Cli("dll_written", outputPath, $"{dllSize:N0}"), sw.Elapsed);
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
