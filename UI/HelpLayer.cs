namespace ECAssistant.UI;

/// <summary>
/// Full-screen help layer.
/// Shows formatted help content, waits for Enter, then pops back.
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
        // Only pop on Enter — user must consciously press Enter to return
        if (key.Key == ConsoleKey.Enter)
            return false; // pop the layer

        // All other keys: handled (stay in help layer)
        return true;
    }

    private string[] BuildHelpContent()
    {
        var lines = new List<string>
        {
            $"{_color.Cyan}{_color.Bold}  ECAssistant — Help{_color.Reset}",
            $"{_color.Dim}  Press Enter to return{_color.Reset}",
            ""
        };

        foreach (var line in _helpLines)
            lines.Add(line);

        return lines.ToArray();
    }
}