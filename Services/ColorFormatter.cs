using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// ANSI color code formatting.
/// Implements IColorFormatter for dependency injection.
/// </summary>
public class ColorFormatter : IColorFormatter
{
    public string Reset => "\x1b[0m";
    public string Bold => "\x1b[1m";
    public string Dim => "\x1b[2m";

    public string Black => "\x1b[30m";
    public string Red => "\x1b[31m";
    public string Green => "\x1b[32m";
    public string Blue => "\x1b[34m";
    public string Yellow => "\x1b[33m";
    public string Cyan => "\x1b[36m";
    public string White => "\x1b[37m";
    public string Magenta => "\x1b[35m";

    public string Format(string color, string text)
    {
        var colorCode = color switch
        {
            "red" => Red,
            "green" => Green,
            "blue" => Blue,
            "yellow" => Yellow,
            "cyan" => Cyan,
            "white" => White,
            _ => Reset
        };

        return $"{colorCode}{text}{Reset}";
    }

    public void Write(string color, string text)
    {
        Console.Write(color + text + Reset);
    }

    public void WriteLine(string color, string text)
    {
        Console.WriteLine(color + text + Reset);
    }

    public void Tag(string tagColor, string tag, string msg)
        => WriteLine(tagColor, $"[{tag}] {msg}");

    public void TagBold(string tagColor, string tag, string msg)
        => WriteLine(Bold + tagColor, $"[{tag}] {msg}");
}