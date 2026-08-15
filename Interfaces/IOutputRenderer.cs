namespace ECAssistant.Interfaces;

/// <summary>
/// Terminal output abstraction.
/// </summary>
public interface IOutputRenderer
{
    void Write(string text);
    void WriteLine(string text);
    void WriteColored(string color, string text);
    void WriteColoredLine(string color, string text);
    void Flush();
}