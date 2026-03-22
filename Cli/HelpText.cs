using AuraLang.I18n;

namespace AuraLang.Cli;

internal static class HelpText
{
    public static void PrintUsage()
        => Console.WriteLine(Msg.Cli("help_main"));

    public static void PrintCompile()
        => Console.WriteLine(Msg.Cli("help_compile"));

    public static void PrintRun()
        => Console.WriteLine(Msg.Cli("help_run"));

    public static void PrintCheck()
        => Console.WriteLine(Msg.Cli("help_check"));
}
