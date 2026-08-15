namespace ECAssistant;

/// <summary>
/// ANSI color codes. Used ONLY by ConsoleUiRenderer (the UI bridge).
/// Nothing else in the codebase should touch this class.
/// </summary>
public class EColor
{
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
}