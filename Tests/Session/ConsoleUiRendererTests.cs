using ECAssistant.Session;
using ECAssistant.UI;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Session;

public class ConsoleUiRendererTests
{
    private readonly Mock<EGuiBase> _mockGui;
    private readonly Mock<IColorFormatter> _mockColor;
    private readonly ConsoleUiRenderer _renderer;

    public ConsoleUiRendererTests()
    {
        _mockGui = new Mock<EGuiBase>();
        _mockColor = new Mock<IColorFormatter>();

        // Setup color properties
        _mockColor.SetupGet(c => c.Cyan).Returns("\x1b[36m");
        _mockColor.SetupGet(c => c.Green).Returns("\x1b[32m");
        _mockColor.SetupGet(c => c.Yellow).Returns("\x1b[33m");
        _mockColor.SetupGet(c => c.Red).Returns("\x1b[31m");
        _mockColor.SetupGet(c => c.Dim).Returns("\x1b[2m");
        _mockColor.SetupGet(c => c.Bold).Returns("\x1b[1m");
        _mockColor.SetupGet(c => c.Reset).Returns("\x1b[0m");
        _mockColor.SetupGet(c => c.Magenta).Returns("\x1b[35m");

        _renderer = new ConsoleUiRenderer(_mockGui.Object, _mockColor.Object);
    }

    [Fact]
    public void OnOutput_StreamEntry_CallsWriteLineColored()
    {
        var entry = new OutputEntry
        {
            Type = "stream",
            Text = "Hello world",
            State = OutputState.Info
        };

        _renderer.OnOutput(entry);

        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Hello world"))), Times.Once);
    }

    [Fact]
    public void OnOutput_StreamEntryEmptyText_DoesNotCallWriteLine()
    {
        var entry = new OutputEntry { Type = "stream", Text = "", State = OutputState.Info };
        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnOutput_LineEntry_WithText_CallsWriteLineColored()
    {
        var entry = new OutputEntry
        {
            Type = "line",
            Text = "Some message",
            State = OutputState.Info
        };

        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Some message"))), Times.Once);
    }

    [Fact]
    public void OnOutput_LineEntryEmpty_CallsBlankLine()
    {
        var entry = new OutputEntry { Type = "line", Text = "", State = OutputState.Info };
        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.BlankLine(), Times.Once);
    }

    [Fact]
    public void OnOutput_LineEntryWarning_AddsWarnTag()
    {
        var entry = new OutputEntry
        {
            Type = "line",
            Text = "Be careful",
            State = OutputState.Warning
        };

        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[WARN]") && s.Contains("Be careful"))), Times.Once);
    }

    [Fact]
    public void OnOutput_LineEntryError_AddsErrTag()
    {
        var entry = new OutputEntry
        {
            Type = "line",
            Text = "Something broke",
            State = OutputState.Error
        };

        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[ERR]") && s.Contains("Something broke"))), Times.Once);
    }

    [Fact]
    public void OnOutput_LineEntrySuccess_AddsOkTag()
    {
        var entry = new OutputEntry
        {
            Type = "line",
            Text = "It worked",
            State = OutputState.Success
        };

        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[OK]") && s.Contains("It worked"))), Times.Once);
    }

    [Fact]
    public void OnOutput_LineEntrySystem_AddsSysTag()
    {
        var entry = new OutputEntry
        {
            Type = "line",
            Text = "System message",
            State = OutputState.System
        };

        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[SYS]") && s.Contains("System message"))), Times.Once);
    }

    [Fact]
    public void OnOutput_LineEntryInfo_NoTag()
    {
        var entry = new OutputEntry
        {
            Type = "line",
            Text = "Just info",
            State = OutputState.Info
        };

        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => !s.Contains("[OK]") && !s.Contains("[WARN]") && !s.Contains("[ERR]") && !s.Contains("[SYS]") && s.Contains("Just info"))), Times.Once);
    }

    [Fact]
    public void OnOutput_ToolOutput_CallsWriteLineColoredWithDim()
    {
        var entry = new OutputEntry
        {
            Type = "tool_output",
            Text = "Tool result",
            State = OutputState.Dim
        };

        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Tool result"))), Times.Once);
    }

    [Fact]
    public void OnOutput_ThinkingEntry_AddsThinkingEmoji()
    {
        var entry = new OutputEntry
        {
            Type = "thinking",
            Text = "Let me consider...",
            State = OutputState.Dim
        };

        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("💭") && s.Contains("Let me consider"))), Times.Once);
    }

    [Fact]
    public void OnOutput_RawToken_DoesNotCallWrite()
    {
        var entry = new OutputEntry { Type = "raw_token", Text = "token", State = OutputState.Raw };
        _renderer.OnOutput(entry);
        _mockGui.Verify(g => g.WriteLineColored(It.IsAny<string>()), Times.Never);
        _mockGui.Verify(g => g.BlankLine(), Times.Never);
    }

    [Fact]
    public void OnQueueChanged_DoesNotThrow()
    {
        _renderer.OnQueueChanged(new List<string> { "prompt1", "prompt2" });
        // No exception expected — method is a no-op
        Assert.True(true);
    }

    [Fact]
    public void OnStateChanged_DoesNotThrow()
    {
        _renderer.OnStateChanged(SessionRunState.Running);
        // No exception expected — method is a no-op
        Assert.True(true);
    }

    [Fact]
    public void RenderHistory_StreamEntries_CallsWriteLineColored()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "stream", Text = "Line 1", State = OutputState.Info },
            new() { Type = "stream", Text = "Line 2", State = OutputState.Success },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Line 1"))), Times.Once);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Line 2"))), Times.Once);
    }

    [Fact]
    public void RenderHistory_LineEntries_CallsWriteLineColored()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "Message 1", State = OutputState.Warning },
            new() { Type = "line", Text = "Message 2", State = OutputState.Error },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[WARN]") && s.Contains("Message 1"))), Times.Once);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[ERR]") && s.Contains("Message 2"))), Times.Once);
    }

    [Fact]
    public void RenderHistory_EmptyLineEntry_CallsBlankLine()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "", State = OutputState.Info },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.BlankLine(), Times.Once);
    }

    [Fact]
    public void RenderHistory_RawTokenEntries_AreSkipped()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "raw_token", Text = "tok", State = OutputState.Raw },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.WriteLineColored(It.IsAny<string>()), Times.Never);
        _mockGui.Verify(g => g.BlankLine(), Times.Never);
    }

    [Fact]
    public void RenderHistory_EmptyStreamText_SkipsEntry()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "stream", Text = "", State = OutputState.Info },
            new() { Type = "stream", Text = "Has content", State = OutputState.Info },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Has content"))), Times.Once);
    }

    [Fact]
    public void RenderHistory_SuccessLine_AddsOkTag()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "Done", State = OutputState.Success },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[OK]"))), Times.Once);
    }

    [Fact]
    public void RenderHistory_SystemLine_AddsSysTag()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "Booting", State = OutputState.System },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[SYS]"))), Times.Once);
    }

    [Fact]
    public void RenderHistory_InfoLine_NoTag()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "Hello", State = OutputState.Info },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => !s.Contains("[OK]") && !s.Contains("[WARN]") && !s.Contains("[ERR]") && !s.Contains("[SYS]") && s.Contains("Hello"))), Times.Once);
    }

    [Fact]
    public void RenderHistory_MultipleEntries_AllRendered()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "First", State = OutputState.Info },
            new() { Type = "line", Text = "", State = OutputState.Info },
            new() { Type = "stream", Text = "Second", State = OutputState.Success },
            new() { Type = "line", Text = "Third", State = OutputState.Warning },
        };

        _renderer.RenderHistory(_mockGui.Object, entries, _mockColor.Object);

        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("First"))), Times.Once);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Second"))), Times.Once);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Third"))), Times.Once);
        _mockGui.Verify(g => g.BlankLine(), Times.Once);
    }
}