using AuraLang.I18n;

namespace AuraLang.Cli;

/// <summary>
/// Centralises all terminal output for the Aura CLI.
/// Provides coloured phase headers, diagnostic lines, source-context snippets,
/// and a final summary line.
/// </summary>
internal static class ConsoleWriter
{
    // ── Unicode support detection ─────────────────────────────────────────────
    // Some Windows consoles do not support ✓ / ✗.
    // We probe once at startup and fall back to ASCII alternatives.
    private static readonly bool _unicode = ProbeUnicode();

    private static bool ProbeUnicode()
    {
        try
        {
            // CP 65001 = UTF-8; most modern Windows terminals support it.
            return Console.OutputEncoding.CodePage == 65001
                || Console.OutputEncoding.EncodingName.Contains("Unicode", StringComparison.OrdinalIgnoreCase)
                || !OperatingSystem.IsWindows();
        }
        catch
        {
            return false;
        }
    }

    private static string Ok   => _unicode ? "✓" : "OK";
    private static string Fail => _unicode ? "✗" : "FAIL";

    // ── Phase headers ─────────────────────────────────────────────────────────

    /// <summary>Prints "[N] PhaseName..."</summary>
    public static void PhaseHeader(int stepNumber, string name)
    {
        Console.WriteLine();
        SetColor(ConsoleColor.DarkCyan);
        Console.Write($"  [{stepNumber}] {name}");
        Console.Write("...");
        Reset();
        Console.WriteLine();
    }

    // ── Phase results ─────────────────────────────────────────────────────────

    /// <summary>Prints "  ✓ {message}  ({ms} ms)" in green.</summary>
    public static void PhaseOk(string message, TimeSpan elapsed)
    {
        SetColor(ConsoleColor.Green);
        Console.Write($"      {Ok} {message}");
        SetColor(ConsoleColor.DarkGray);
        Console.WriteLine($"  ({elapsed.TotalMilliseconds:F0} ms)");
        Reset();
    }

    /// <summary>Prints "  ✗ {message}  ({ms} ms)" in red.</summary>
    public static void PhaseFail(string message, TimeSpan elapsed)
    {
        SetColor(ConsoleColor.Red);
        Console.Error.Write($"      {Fail} {message}");
        SetColor(ConsoleColor.DarkGray);
        Console.Error.WriteLine($"  ({elapsed.TotalMilliseconds:F0} ms)");
        Reset();
    }

    // ── Diagnostic lines ──────────────────────────────────────────────────────

    /// <summary>Prints an error diagnostic line to stderr in red.</summary>
    public static void DiagnosticError(string formatted)
    {
        SetColor(ConsoleColor.Red);
        Console.Error.WriteLine("  " + formatted);
        Reset();
    }

    /// <summary>Prints a warning diagnostic line in yellow.</summary>
    public static void DiagnosticWarning(string formatted)
    {
        SetColor(ConsoleColor.Yellow);
        Console.WriteLine("  " + formatted);
        Reset();
    }

    /// <summary>Prints an info/note diagnostic line in dark gray.</summary>
    public static void DiagnosticInfo(string formatted)
    {
        SetColor(ConsoleColor.DarkGray);
        Console.WriteLine("  " + formatted);
        Reset();
    }

    // ── Top-level messages ────────────────────────────────────────────────────

    /// <summary>Prints "error: {message}" to stderr in red.</summary>
    public static void Error(string message)
    {
        SetColor(ConsoleColor.Red);
        Console.Error.WriteLine($"error: {message}");
        Reset();
    }

    /// <summary>Prints a plain informational line.</summary>
    public static void Info(string message)
        => Console.WriteLine(message);

    // ── Verbose-gated output ──────────────────────────────────────────────────

    public static void Verbose(bool enabled, string message)
    {
        if (!enabled) return;
        SetColor(ConsoleColor.DarkGray);
        Console.WriteLine("  " + message);
        Reset();
    }

    // ── Source-context snippet ────────────────────────────────────────────────

    /// <summary>
    /// Shows the source line with a caret pointer underneath, e.g.:
    /// <code>
    ///    12 |     let x = foo(
    ///                         ^
    /// </code>
    /// <paramref name="oneBasedLine"/> is 1-based (ANTLR line convention).
    /// <paramref name="zeroBasedCol"/> is 0-based (ANTLR column convention).
    /// </summary>
    public static void SourceContext(string[] sourceLines, int oneBasedLine, int zeroBasedCol)
    {
        if (oneBasedLine <= 0 || oneBasedLine > sourceLines.Length) return;

        var line = sourceLines[oneBasedLine - 1].TrimEnd('\r');
        var lineNo = oneBasedLine.ToString();
        var prefix = $"  {lineNo,4} | ";
        var caretPad = new string(' ', prefix.Length + Math.Max(0, zeroBasedCol));
        var caret = "^";

        SetColor(ConsoleColor.DarkGray);
        Console.Error.WriteLine(prefix + line);
        SetColor(ConsoleColor.Cyan);
        Console.Error.WriteLine(caretPad + caret);
        Reset();
    }

    // ── Summary line ──────────────────────────────────────────────────────────

    /// <summary>
    /// Prints a coloured summary: "N error(s), M warning(s) — T ms total".
    /// </summary>
    public static void Summary(int errors, int warnings, TimeSpan total)
    {
        Console.WriteLine();

        if (errors > 0)
            SetColor(ConsoleColor.Red);
        else if (warnings > 0)
            SetColor(ConsoleColor.Yellow);
        else
            SetColor(ConsoleColor.Green);

        var errPart  = Msg.Cli("summary_errors", errors);
        var warnPart = Msg.Cli("summary_warnings", warnings);
        Console.Write($"  {errPart}, {warnPart}");

        SetColor(ConsoleColor.DarkGray);
        Console.WriteLine($"  —  {Msg.Cli("summary_total_ms", $"{total.TotalMilliseconds:F0}")}");
        Reset();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void SetColor(ConsoleColor c)
    {
        try { Console.ForegroundColor = c; }
        catch { /* non-interactive / piped output */ }
    }

    private static void Reset()
    {
        try { Console.ResetColor(); }
        catch { }
    }
}
