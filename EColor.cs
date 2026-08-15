using ECAssistant.Interfaces;

namespace ECAssistant;

/// <summary>Simple ANSI-based console coloring for ECAssistant.
/// Instance class implementing IColorFormatter for OOP dependency injection.</summary>
public class EColor : IColorFormatter
{
    // ── Color constants (instance properties) ──
    public string Reset      => "\x1b[0m";
    public string Bold       => "\x1b[1m";
    public string Dim        => "\x1b[2m";

    public string Black    => "\x1b[30m";
    public string Red      => "\x1b[31m";
    public string Green    => "\x1b[32m";
    public string Yellow   => "\x1b[33m";
    public string Blue      => "\x1b[34m";
    public string Magenta  => "\x1b[35m";
    public string Cyan      => "\x1b[36m";
    public string White    => "\x1b[37m";

    // ── Variant helpers — returns combo of color + bold for readability ──
    public string Cfg()          => Yellow;
    public string Info()         => Cyan;
    public string Success()      => Green;
    public string Error()        => Red;
    public string Warn()         => Yellow;
    public string Model()        => Blue;
    public string ToolCall()     => Magenta;
    public string Token()        => Dim;

    // ── Output redirect ──
    // Route EColor output through a delegate so EGuiConsole can
    // intercept it for ANSI scroll region cursor management.
    // Default: Console.WriteLine/Write (fallback when no UI is set)
    public Action<string>? WriteHandler { get; set; }
    public Action<string>? WriteLineHandler { get; set; }

    // ── IColorFormatter.Format (existing) ──
    public string Format(string color, string text)
    {
        return $"{color}{text}{Reset}";
    }

    // ── Write / WriteLine ──
    public void Write(string color, string text)
    {
        if (WriteHandler != null)
            WriteHandler(color + text + Reset);
        else
            Console.Write(color + text + Reset);
    }

    public void WriteLine(string color, string text)
    {
        if (WriteLineHandler != null)
            WriteLineHandler(color + text + Reset);
        else
            Console.WriteLine(color + text + Reset);
    }

    // ── Tag / TagBold ──
    public void Tag(string tagColor, string tag, string msg)
        => WriteLine(tagColor, $"[{tag}] {msg}");

    public void TagBold(string tagColor, string tag, string msg)
        => WriteLine(Bold + tagColor, $"[{tag}] {msg}");
}