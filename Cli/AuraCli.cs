using System.Reflection;
using AuraLang.I18n;

namespace AntlrCompiler.Cli;

/// <summary>
/// Top-level CLI router.
/// Parses the subcommand, dispatches to the appropriate command handler,
/// and returns an integer exit code.
/// </summary>
internal static class AuraCli
{
    private static readonly string Version = GetVersion();

    // ── Entry point ──────────────────────────────────────────────────────────

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            HelpText.PrintUsage();
            return 0;
        }

        return args[0] switch
        {
            "compile"           => RunCompile(args[1..]),
            "run"               => RunRun(args[1..]),
            "check"             => RunCheck(args[1..]),
            "lsp"               => LspCommand.Execute(),
            "version"           => RunVersion(),
            "-V" or "--version" => RunVersion(),
            "-h" or "--help"    => RunHelp(),
            _                   => UnknownSubcommand(args[0]),
        };
    }

    // ── Subcommand entry points ───────────────────────────────────────────────

    private static int RunCompile(string[] args)
    {
        if (HasHelp(args)) { HelpText.PrintCompile(); return 0; }
        if (!TryParseCompile(args, out var opts, out var err))
        {
            ConsoleWriter.Error(err!);
            return 1;
        }
        InitLocale(opts!.Lang);
        return CompileCommand.Execute(opts!);
    }

    private static int RunRun(string[] args)
    {
        if (HasHelp(args)) { HelpText.PrintRun(); return 0; }
        if (!TryParseRun(args, out var opts, out var err))
        {
            ConsoleWriter.Error(err!);
            return 1;
        }
        InitLocale(opts!.Lang);
        return RunCommand.Execute(opts!);
    }

    private static int RunCheck(string[] args)
    {
        if (HasHelp(args)) { HelpText.PrintCheck(); return 0; }
        if (!TryParseCheck(args, out var opts, out var err))
        {
            ConsoleWriter.Error(err!);
            return 1;
        }
        InitLocale(opts!.Lang);
        return CheckCommand.Execute(opts!);
    }

    private static int RunVersion()
    {
        Console.WriteLine($"aura {Version}");
        return 0;
    }

    private static int RunHelp()
    {
        HelpText.PrintUsage();
        return 0;
    }

    private static int UnknownSubcommand(string name)
    {
        ConsoleWriter.Error(Msg.Cli("unknown_subcommand", name));
        return 1;
    }

    // ── Locale initialization ───────────────────────────────────────────────

    private static void InitLocale(string? lang)
    {
        LocaleContext.Current = LocaleContext.Detect(lang);
    }

    // ── Argument parsers ─────────────────────────────────────────────────────

    private static bool TryParseCompile(
        string[] args,
        out CompileOptions? result,
        out string? error)
    {
        result = null;
        string? sourceFile = null;
        string? outputPath = null;
        string? name       = null;
        string? lang       = null;
        bool verbose       = false;
        bool noLower       = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--out":
                    if (++i >= args.Length) { error = Msg.Cli("out_requires_path"); return false; }
                    outputPath = args[i];
                    break;

                case "--name":
                    if (++i >= args.Length) { error = Msg.Cli("name_requires_arg"); return false; }
                    name = args[i];
                    break;

                case "--lang":
                    if (++i >= args.Length) { error = "--lang requires a language argument."; return false; }
                    lang = args[i];
                    break;

                case "-v" or "--verbose":
                    verbose = true;
                    break;

                case "--no-lower":
                    noLower = true;
                    break;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        error = Msg.Cli("unknown_option", args[i], "compile");
                        return false;
                    }
                    if (sourceFile is not null)
                    {
                        error = Msg.Cli("unexpected_arg", args[i]);
                        return false;
                    }
                    sourceFile = args[i];
                    break;
            }
        }

        if (sourceFile is null) { error = Msg.Cli("no_source_file"); return false; }

        result = new CompileOptions(sourceFile, outputPath, name, verbose, noLower, lang);
        error  = null;
        return true;
    }

    private static bool TryParseRun(
        string[] args,
        out RunOptions? result,
        out string? error)
    {
        result = null;
        string? sourceFile = null;
        string? outputPath = null;
        string? name       = null;
        string? lang       = null;
        bool verbose       = false;
        bool noLower       = false;
        string tfm         = "net10.0";
        bool selfContained = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--out":
                    if (++i >= args.Length) { error = Msg.Cli("out_requires_path"); return false; }
                    outputPath = args[i];
                    break;

                case "--name":
                    if (++i >= args.Length) { error = Msg.Cli("name_requires_arg"); return false; }
                    name = args[i];
                    break;

                case "--lang":
                    if (++i >= args.Length) { error = "--lang requires a language argument."; return false; }
                    lang = args[i];
                    break;

                case "-v" or "--verbose":
                    verbose = true;
                    break;

                case "--no-lower":
                    noLower = true;
                    break;

                case "--target":
                    if (++i >= args.Length) { error = Msg.Cli("target_requires_tfm"); return false; }
                    tfm = args[i];
                    break;

                case "--self-contained":
                    selfContained = true;
                    break;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        error = Msg.Cli("unknown_option", args[i], "run");
                        return false;
                    }
                    if (sourceFile is not null)
                    {
                        error = Msg.Cli("unexpected_arg", args[i]);
                        return false;
                    }
                    sourceFile = args[i];
                    break;
            }
        }

        if (sourceFile is null) { error = Msg.Cli("no_source_file"); return false; }

        result = new RunOptions(sourceFile, outputPath, name, verbose, noLower, lang, tfm, selfContained);
        error  = null;
        return true;
    }

    private static bool TryParseCheck(
        string[] args,
        out CheckOptions? result,
        out string? error)
    {
        result = null;
        string? sourceFile = null;
        string? lang       = null;
        bool verbose       = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-v" or "--verbose":
                    verbose = true;
                    break;

                case "--lang":
                    if (++i >= args.Length) { error = "--lang requires a language argument."; return false; }
                    lang = args[i];
                    break;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        error = Msg.Cli("unknown_option", args[i], "check");
                        return false;
                    }
                    if (sourceFile is not null)
                    {
                        error = Msg.Cli("unexpected_arg", args[i]);
                        return false;
                    }
                    sourceFile = args[i];
                    break;
            }
        }

        if (sourceFile is null) { error = Msg.Cli("no_source_file"); return false; }

        result = new CheckOptions(sourceFile, verbose, lang);
        error  = null;
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool HasHelp(string[] args)
        => Array.Exists(args, a => a is "-h" or "--help");

    private static string GetVersion()
    {
        try
        {
            var attr = typeof(AuraCli).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion ?? "0.1.0";
        }
        catch
        {
            return "0.1.0";
        }
    }
}
