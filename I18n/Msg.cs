namespace AuraLang.I18n;

/// <summary>
/// Single entry point for all localized messages.
/// Usage: Msg.Diag("AUR1010", fullName) or Msg.Cli("file_not_found", path)
/// </summary>
public static class Msg
{
    public static string Diag(string code, params object[] args) =>
        DiagnosticMessages.Get(code, LocaleContext.Current, args);

    public static string Cli(string key, params object[] args) =>
        CliMessages.Get(key, LocaleContext.Current, args);
}
