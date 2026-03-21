using System;
using System.Globalization;

namespace AuraLang.I18n;

public enum AuraLocale { En, Ja, Zh }

public static class LocaleContext
{
    /// <summary>
    /// Current locale for all user-facing messages.
    /// Set once at CLI startup, then read-only during compilation.
    /// </summary>
    public static AuraLocale Current { get; set; } = AuraLocale.En;

    /// <summary>
    /// Resolve locale from --lang flag, AURA_LANG env var, or system culture.
    /// Priority: explicit flag > env var > system culture > English default.
    /// </summary>
    public static AuraLocale Detect(string? explicitLang)
    {
        if (explicitLang is not null)
            return Parse(explicitLang);

        var env = Environment.GetEnvironmentVariable("AURA_LANG");
        if (env is not null)
            return Parse(env);

        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Parse(culture);
    }

    private static AuraLocale Parse(string s) => s.ToLowerInvariant() switch
    {
        "ja" or "jp" or "ja-jp" => AuraLocale.Ja,
        "zh" or "cn" or "zh-cn" or "zh-tw" => AuraLocale.Zh,
        _ => AuraLocale.En,
    };
}
