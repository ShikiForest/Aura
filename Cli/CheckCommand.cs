using System.Diagnostics;
using AuraLang.I18n;
using AuraLang.Semantics;

namespace AntlrCompiler.Cli;

/// <summary>
/// Implements `aura check`: parse + semantic analysis only.
/// No output files are produced.  Exits 0 on success, 1 on any error.
/// </summary>
internal static class CheckCommand
{
    public static int Execute(CheckOptions opts)
    {
        var totalSw = Stopwatch.StartNew();
        int errors = 0, warnings = 0;

        // ── Load source ───────────────────────────────────────────────────────
        if (!File.Exists(opts.SourceFile))
        {
            ConsoleWriter.Error(Msg.Cli("file_not_found", opts.SourceFile));
            return 1;
        }

        var source      = File.ReadAllText(opts.SourceFile);
        var sourceLines = source.Split('\n');

        Console.WriteLine($"  {Msg.Cli("label_source")} : {opts.SourceFile}");

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
            totalSw.Stop();
            ConsoleWriter.Summary(errors, warnings, totalSw.Elapsed);
            return 1;
        }

        ConsoleWriter.PhaseOk(Msg.Cli("ast_built"), sw.Elapsed);

        // ── Step 2: Semantic analysis ─────────────────────────────────────────
        ConsoleWriter.PhaseHeader(2, Msg.Cli("phase_semantic"));
        sw = Stopwatch.StartNew();
        var semResult = SemanticFrontEnd.Check(parseResult.Ast);
        sw.Stop();

        int semErrors = 0;
        foreach (var d in semResult.Diagnostics)
        {
            var fd = DiagnosticFormatter.FromSemantic(d);
            DiagnosticFormatter.Emit(fd, opts.SourceFile, sourceLines, opts.Verbose);
            if (fd.Level == DiagLevel.Error)        semErrors++;
            else if (fd.Level == DiagLevel.Warning) warnings++;
        }
        errors += semErrors;

        if (semErrors > 0)
            ConsoleWriter.PhaseFail(Msg.Cli("semantic_found_errors"), sw.Elapsed);
        else
            ConsoleWriter.PhaseOk(Msg.Cli("n_diagnostics", semResult.Diagnostics.Count), sw.Elapsed);

        totalSw.Stop();
        ConsoleWriter.Summary(errors, warnings, totalSw.Elapsed);
        return errors > 0 ? 1 : 0;
    }
}
