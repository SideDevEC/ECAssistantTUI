using ECAssistant.Core;
namespace ECAssistant.TUI.UI;

/// <summary>
/// Static help content layer. Shows the help screen with all available commands.
/// No live updates, no streaming. Controller swaps back to the prior active layer
/// on ESC or Enter.
/// </summary>
public sealed class HelpLayer : BaseLayer
{
    private readonly EColor _color;
    private readonly string[] _helpLines;
    
    public override string Name => "help";
    
    public HelpLayer(EColor color, string[] helpLines)
    {
        _color = color;
        _helpLines = helpLines;
    }
    
    /// <summary>
    /// Build the help content and populate the output buffer.
    /// Called by the controller when creating/activating this layer.
    /// </summary>
    public void BuildContent()
    {
        Clear();
        AddOutputLine($"{_color.Cyan}{_color.Bold}  ECAssistant — Menu{_color.Reset}");
        AddOutputLine($"{_color.Dim}  ESC = back{_color.Reset}");
        AddOutputLine("");
        foreach (var line in _helpLines)
            AddOutputLine(line);
    }
    
    public override bool ProcessInput(string input)
    {
        // Let base handle common commands (/clear)
        if (base.ProcessInput(input)) return true;
        
        // Any other input (Enter with empty, or any text) → signal return
        // The controller handles the actual layer switch back
        return false;
    }
}