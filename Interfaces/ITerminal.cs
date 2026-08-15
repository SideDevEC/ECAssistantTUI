namespace ECAssistant.Interfaces;

/// <summary>
/// Terminal I/O abstraction.
/// </summary>
public interface ITerminal
{
    void Write(string text);
    string ReadLine();
    int Width { get; }
    int Height { get; }
    void Clear();
}