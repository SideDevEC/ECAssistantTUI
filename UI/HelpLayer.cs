namespace ECAssistant.UI;

/// <summary>
/// Full-screen help layer.
/// Shows formatted help content, waits for any keypress, then pops back.
/// </summary>
public sealed class HelpLayer : IGuiLayer
{
    private readonly EColor _color;
    private readonly string[] _helpLines;

    public string Name => "help";

    public HelpLayer(EColor color, string[] helpLines)
    {
        _color = color;
        _helpLines = helpLines;
    }

    public void OnActivate(EGuiConsole console)
    {
        console.PaintLayerScreen(BuildHelpContent());
    }

    public void OnResize(EGuiConsole console)
    {
        console.PaintLayerScreen(BuildHelpContent());
    }

    public bool OnKey(EGuiConsole console, ConsoleKeyInfo key)
    {
        // Any key pops the help layer
        return false;
    }

    private string[] BuildHelpContent()
    {
        var lines = new List<string>();

        lines.Add($"{_color.Cyan}{_color.Bold}  ECAssistant — Help{_color.Reset}");
        lines.Add($"{_color.Dim}  Press any key to return{_color.Reset}");
        lines.Add("");

        foreach (var line in _helpLines)
            lines.Add(line);

        return lines.ToArray();
    }
}