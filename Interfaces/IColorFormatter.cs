namespace ECAssistant.Interfaces;

/// <summary>
/// Color formatting interface — provides ANSI color codes and output methods.
/// Implemented by EColor and ColorFormatter.
/// </summary>
public interface IColorFormatter
{
    // ── Existing ──
    string Format(string color, string text);
    string Red { get; }
    string Green { get; }
    string Blue { get; }
    string Yellow { get; }
    string Cyan { get; }
    string White { get; }

    // ── New color properties ──
    string Reset { get; }
    string Bold { get; }
    string Dim { get; }
    string Black { get; }
    string Magenta { get; }

    // ── New output methods ──
    void Write(string color, string text);
    void WriteLine(string color, string text);
    void Tag(string tagColor, string tag, string msg);
    void TagBold(string tagColor, string tag, string msg);
}