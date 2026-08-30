using ECAssistant.Core.Setup;

using ECAssistant.TUI.UI;
using EColor = ECAssistant.Core.EColor;
namespace ECAssistant.TUI.Controller;

/// <summary>
/// Adapts the TUI console to the staged installer wizard's ISetupUi abstraction,
/// so /reinstall and the initial install share the exact same flow.
/// </summary>
public sealed class TuiSetupUi : ISetupUi
{
    private static readonly EColor Color = new();
    private readonly IGuiConsole _console;

    public TuiSetupUi(IGuiConsole console) => _console = console;

    public void WriteLine(string text = "") => _console.WriteLine(text);

    public void Write(string text) => _console.WriteRaw(text);

    public string? ReadLine() => _console.PromptRaw("");

    public void WriteLineGreen(string text) => _console.WriteLineColored($"{Color.Green}{text}{Color.Reset}");
}
