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
    public void OnOutput_WithText_CallsWriteLineColored()
    {
        _renderer.OnOutput("Hello world", OutputState.Info);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("Hello world"))), Times.Once);
    }

    [Fact]
    public void OnOutput_EmptyText_CallsBlankLine()
    {
        _renderer.OnOutput("", OutputState.Info);
        _mockGui.Verify(g => g.BlankLine(), Times.Once);
    }

    [Fact]
    public void OnOutput_Warning_AddsWarnTag()
    {
        _renderer.OnOutput("Be careful", OutputState.Warning);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[WARN]") && s.Contains("Be careful"))), Times.Once);
    }

    [Fact]
    public void OnOutput_Error_AddsErrTag()
    {
        _renderer.OnOutput("Something broke", OutputState.Error);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[ERR]") && s.Contains("Something broke"))), Times.Once);
    }

    [Fact]
    public void OnOutput_Success_AddsOkTag()
    {
        _renderer.OnOutput("It worked", OutputState.Success);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[OK]") && s.Contains("It worked"))), Times.Once);
    }

    [Fact]
    public void OnOutput_System_AddsSysTag()
    {
        _renderer.OnOutput("System message", OutputState.System);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => s.Contains("[SYS]") && s.Contains("System message"))), Times.Once);
    }

    [Fact]
    public void OnOutput_Info_NoTag()
    {
        _renderer.OnOutput("Just info", OutputState.Info);
        _mockGui.Verify(g => g.WriteLineColored(It.Is<string>(s => !s.Contains("[OK]") && !s.Contains("[WARN]") && !s.Contains("[ERR]") && !s.Contains("[SYS]") && s.Contains("Just info"))), Times.Once);
    }

    [Fact]
    public void OnStreamStart_DoesNotThrow()
    {
        _renderer.OnStreamStart();
        Assert.True(true);
    }

    [Fact]
    public void OnStreamStop_DoesNotThrow()
    {
        _renderer.OnStreamStop();
        Assert.True(true);
    }

    [Fact]
    public void OnRequestApproval_YesResponse_ReturnsTrue()
    {
        _mockGui.Setup(g => g.PromptRaw(It.IsAny<string>())).Returns("y");
        var result = _renderer.OnRequestApproval("Approve?");
        Assert.True(result);
    }

    [Fact]
    public void OnRequestApproval_NoResponse_ReturnsFalse()
    {
        _mockGui.Setup(g => g.PromptRaw(It.IsAny<string>())).Returns("n");
        var result = _renderer.OnRequestApproval("Approve?");
        Assert.False(result);
    }

    [Fact]
    public void OnRequestApproval_YesFullResponse_ReturnsTrue()
    {
        _mockGui.Setup(g => g.PromptRaw(It.IsAny<string>())).Returns("yes");
        var result = _renderer.OnRequestApproval("Approve?");
        Assert.True(result);
    }

    [Fact]
    public void OnRequestApproval_EmptyResponse_ReturnsFalse()
    {
        _mockGui.Setup(g => g.PromptRaw(It.IsAny<string>())).Returns("");
        var result = _renderer.OnRequestApproval("Approve?");
        Assert.False(result);
    }

    [Fact]
    public void OnRequestApproval_NullResponse_ReturnsFalse()
    {
        _mockGui.Setup(g => g.PromptRaw(It.IsAny<string>())).Returns((string?)null);
        var result = _renderer.OnRequestApproval("Approve?");
        Assert.False(result);
    }

    // ── RenderHistory tests (unchanged — method still exists) ──

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