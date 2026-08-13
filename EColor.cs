namespace ECAssistant;

/// <summary>Simple ANSI-based console coloring for ECAssistant.</summary>
public static class EColor
{
    // Color constants — const fields accessible via EColor.Cyan everywhere in namespace
    public const string Reset      = "\x1b[0m";
    public const string Bold       = "\x1b[1m";
    public const string Dim        = "\x1b[2m";

    public const string Black    = "\x1b[30m";
    public const string Red      = "\x1b[31m";
    public const string Green  = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue     = "\x1b[34m";
    public const string Magenta="\x1b[35m";
    public const string Cyan      = "\x1b[36m";
    public const string White    = "\x1b[37m";

    // Variant helpers — returns combo of color + bold for readability
    public static string Cfg()          => Yellow;
    public static string Info()         => Cyan;
    public static string Success()      => Green;
    public static string Error()        => Red;
    public static string Warn()         => Yellow;
    public static string Model()        => Blue;
    public static string ToolCall()     => Magenta;
    public static string Token()        => Dim;

    // ── Output redirect ──
    // v10.21.2: Route EColor output through a delegate so EGuiConsole can
    // intercept it for ANSI scroll region cursor management.
    // Default: Console.WriteLine/Write (fallback when no UI is set)

    public static Action<string>? WriteHandler { get; set; }
    public static Action<string>? WriteLineHandler { get; set; }

    public static void Write(string color, string text)
    {
        if (WriteHandler != null)
            WriteHandler(color + text + Reset);
        else
            Console.Write(color + text + Reset);
    }

    public static void WriteLine(string color, string text)
    {
        if (WriteLineHandler != null)
            WriteLineHandler(color + text + Reset);
        else
            Console.WriteLine(color + text + Reset);
    }

    public static void Tag(string tagColor, string tag, string msg)
           => WriteLine(tagColor, $"[{tag}] {msg}");

    public static void TagBold(string tagColor, string tag, string msg)
           => WriteLine(Bold + tagColor, $"[{tag}] {msg}");
}