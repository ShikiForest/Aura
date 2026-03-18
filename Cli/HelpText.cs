namespace AntlrCompiler.Cli;

internal static class HelpText
{
    public static void PrintUsage()
    {
        Console.WriteLine("""
            Aura compiler  —  usage:
              aura <subcommand> [options]

            SUBCOMMANDS:
              compile <file.aura>   Compile source to a .dll
              run     <file.aura>   Compile, package, and execute
              check   <file.aura>   Parse and type-check only (no output)
              version               Show version

            GLOBAL OPTIONS:
              -h, --help            Show help for a subcommand
              -V, --version         Show version

            Run 'aura <subcommand> --help' for subcommand-specific options.
            """);
    }

    public static void PrintCompile()
    {
        Console.WriteLine("""
            aura compile <file.aura> [options]

            Compiles an Aura source file to a .NET DLL.

            OPTIONS:
              -o, --out <path>      Output DLL path
                                    (default: <file>.dll next to the source)
              --name <name>         Assembly name
                                    (default: source filename without extension)
              -v, --verbose         Verbose output: show all diagnostics + timings
              --no-lower            Skip the lowering phase (debug / stress-test)
              -h, --help            Show this help
            """);
    }

    public static void PrintRun()
    {
        Console.WriteLine("""
            aura run <file.aura> [options]

            Compiles an Aura source file, packages it as an executable,
            and runs it in-process.

            OPTIONS:
              -o, --out <path>      Intermediate DLL path
              --name <name>         Assembly name
              -v, --verbose         Verbose output
              --no-lower            Skip the lowering phase (debug)
              --target <tfm>        Target framework moniker  (default: net10.0)
              --self-contained      Produce a self-contained EXE
              -h, --help            Show this help
            """);
    }

    public static void PrintCheck()
    {
        Console.WriteLine("""
            aura check <file.aura> [options]

            Parses and type-checks a source file without producing any output.
            Exits 0 if there are no errors, 1 otherwise.

            OPTIONS:
              -v, --verbose         Verbose output
              -h, --help            Show this help
            """);
    }
}
