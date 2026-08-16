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
        _outputLines.Clear();
        _outputLines.Add($"{_color.Cyan}{_color.Bold}  ECAssistant — Help{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  Press Enter or ESC to return{_color.Reset}");
        _outputLines.Add("");
        foreach (var line in _helpLines)
            _outputLines.Add(line);
        _scrollOffset = 0;
        _isDirty = true;
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