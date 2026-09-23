using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Tests.UI;

/// <summary>
/// Terminal-restore behavior tests (2026-09-21 fixes): the restore sequence must
/// be written exactly once across multiple shutdown calls (graceful exit + hooks
/// fire repeatedly), and must never be written in non-ANSI mode.
/// </summary>
public sealed class GuiConsoleTerminalRestoreTests
{
    private sealed class RecordingTerminal : ITerminalOutput
    {
        public List<string> Writes { get; } = new();
        public int WindowWidth => 80;
        public int WindowHeight => 24;
#pragma warning disable CS0067
        public event EventHandler? OnResize;
#pragma warning restore CS0067
        public void Write(string text) => Writes.Add(text);
        public void Flush() { }
        public void ClearScreen() { }
        public void SetCursorPosition(int row, int col) { }
        public void ShowCursor() { }
        public void HideCursor() { }
        public void EnableAlternateScreen() { }
        public void DisableAlternateScreen() { }
        public void EnableMouse() { }
        public void DisableMouse() { }
    }

    private static void SetPrivate(GuiConsole console, string field, object value)
    {
        var f = typeof(GuiConsole).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(f);
        f!.SetValue(console, value);
    }

    [Fact]
    public void NonAnsiTerminal_ShutdownConsole_WritesNothing()
    {
        // Test env: Console.IsOutputRedirected → DetectAnsiSupport is false → guard must hold.
        var term = new RecordingTerminal();
        var console = new GuiConsole(term);

        console.ShutdownConsole();

        Assert.Empty(term.Writes);
    }

    [Fact]
    public void AnsiTerminal_ShutdownConsole_WritesRestoreSequenceOnce()
    {
        var term = new RecordingTerminal();
        var console = new GuiConsole(term);
        SetPrivate(console, "_ansiSupported", true);

        // Simulate: graceful shutdown + ProcessExit + CancelKeyPress all firing.
        console.ShutdownConsole();
        console.ShutdownConsole();
        console.ShutdownConsole();

        var all = string.Join("", term.Writes);
        Assert.Contains("\x1b[?1049l", all);   // leave alt screen
        Assert.Contains("\x1b[?25h", all);     // show cursor
        Assert.Contains("\x1b[?1006l", all);   // clear mouse modes
        Assert.Contains("\x1b[H\x1b[2J\x1b[3J", all); // clear screen + scrollback
        // Idempotent: alt-screen leave and final clear appear exactly once.
        Assert.Equal(1, term.Writes.Count(w => w.Contains("\x1b[?1049l")));
        Assert.Equal(1, term.Writes.Count(w => w.Contains("\x1b[H\x1b[2J\x1b[3J")));
    }
}