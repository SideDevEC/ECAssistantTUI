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

        // Print colored text. Always writes with color.
    public static void Write(string color, string text)
           { Console.Write(color + text + "\x1b[0m"); }

    public static void WriteLine(string color, string text)
           { Console.WriteLine(color + text + "\x1b[0m"); }

    public static void Tag(string tagColor, string tag, string msg)
           => WriteLine(tagColor, $"[{tag}] {msg}");

    public static void TagBold(string tagColor, string tag, string msg)
           => WriteLine(Bold + tagColor, $"[{tag}] {msg}");
}
