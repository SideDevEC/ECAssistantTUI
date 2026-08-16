using ECAssistant.Session;
using ECAssistant.UI;

namespace ECAssistant.Tests.Session;

/// <summary>
/// Tests for ConsoleUiRenderer — verifies it writes to SessionLayer's buffer
/// (not to EGuiConsole directly, as in v10.25).
/// </summary>
public class ConsoleUiRendererTests
{
    private readonly SessionLayer _layer;
    private readonly ConsoleUiRenderer _renderer;

    public ConsoleUiRendererTests()
    {
        _layer = new SessionLayer("test");
        _renderer = new ConsoleUiRenderer(_layer, new EColor());
    }

    [Fact]
    public void OnOutput_WithText_AddsToLayerBuffer()
    {
        _renderer.OnOutput("Hello world", OutputState.Info);
        Assert.Single(_layer._outputLines);
        Assert.Contains("Hello world", _layer._outputLines[0]);
    }

    [Fact]
    public void OnOutput_EmptyText_AddsBlankLine()
    {
        _renderer.OnOutput("", OutputState.Info);
        Assert.Single(_layer._outputLines);
        Assert.Equal("", _layer._outputLines[0]);
    }

    [Fact]
    public void OnOutput_Warning_AddsWarnTag()
    {
        _renderer.OnOutput("Be careful", OutputState.Warning);
        Assert.Single(_layer._outputLines);
        Assert.Contains("[WARN]", _layer._outputLines[0]);
        Assert.Contains("Be careful", _layer._outputLines[0]);
    }

    [Fact]
    public void OnOutput_Error_AddsErrTag()
    {
        _renderer.OnOutput("Something broke", OutputState.Error);
        Assert.Single(_layer._outputLines);
        Assert.Contains("[ERR]", _layer._outputLines[0]);
        Assert.Contains("Something broke", _layer._outputLines[0]);
    }

    [Fact]
    public void OnOutput_Success_AddsOkTag()
    {
        _renderer.OnOutput("It worked", OutputState.Success);
        Assert.Single(_layer._outputLines);
        Assert.Contains("[OK]", _layer._outputLines[0]);
        Assert.Contains("It worked", _layer._outputLines[0]);
    }

    [Fact]
    public void OnOutput_System_AddsSysTag()
    {
        _renderer.OnOutput("System message", OutputState.System);
        Assert.Single(_layer._outputLines);
        Assert.Contains("[SYS]", _layer._outputLines[0]);
        Assert.Contains("System message", _layer._outputLines[0]);
    }

    [Fact]
    public void OnOutput_Info_NoTag()
    {
        _renderer.OnOutput("Just info", OutputState.Info);
        Assert.Single(_layer._outputLines);
        var line = _layer._outputLines[0];
        Assert.Contains("Just info", line);
        Assert.DoesNotContain("[OK]", line);
        Assert.DoesNotContain("[WARN]", line);
        Assert.DoesNotContain("[ERR]", line);
        Assert.DoesNotContain("[SYS]", line);
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
    public void OnStreamStop_ClearsLiveStreamLine()
    {
        // Start with some content
        _renderer.OnOutput("Content before stream", OutputState.Info);
        int countBefore = _layer._outputLines.Count;
        
        // Simulate streaming
        _renderer.OnStreamStart();
        _layer.UpdateLiveStreamLine("streaming text");
        Assert.Equal(countBefore + 1, _layer._outputLines.Count);
        
        // Stop streaming — should remove the live stream line
        _renderer.OnStreamStop();
        Assert.Equal(countBefore, _layer._outputLines.Count);
    }

    // ── RenderHistory tests ──

    [Fact]
    public void RenderHistory_StreamEntries_AddsToLayerBuffer()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "stream", Text = "Line 1", State = OutputState.Info },
            new() { Type = "stream", Text = "Line 2", State = OutputState.Success },
        };

        _renderer.RenderHistory(entries);

        Assert.Equal(2, _layer._outputLines.Count);
        Assert.Contains("Line 1", _layer._outputLines[0]);
        Assert.Contains("Line 2", _layer._outputLines[1]);
    }

    [Fact]
    public void RenderHistory_LineEntries_AddsWithTags()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "Message 1", State = OutputState.Warning },
            new() { Type = "line", Text = "Message 2", State = OutputState.Error },
        };

        _renderer.RenderHistory(entries);

        Assert.Equal(2, _layer._outputLines.Count);
        Assert.Contains("[WARN]", _layer._outputLines[0]);
        Assert.Contains("Message 1", _layer._outputLines[0]);
        Assert.Contains("[ERR]", _layer._outputLines[1]);
        Assert.Contains("Message 2", _layer._outputLines[1]);
    }

    [Fact]
    public void RenderHistory_EmptyLineEntry_AddsBlankLine()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "", State = OutputState.Info },
        };

        _renderer.RenderHistory(entries);

        Assert.Single(_layer._outputLines);
        Assert.Equal("", _layer._outputLines[0]);
    }

    [Fact]
    public void RenderHistory_MultipleEntries_AllAddedToBuffer()
    {
        var entries = new List<OutputEntry>
        {
            new() { Type = "line", Text = "First", State = OutputState.Info },
            new() { Type = "line", Text = "", State = OutputState.Info },
            new() { Type = "stream", Text = "Second", State = OutputState.Success },
            new() { Type = "line", Text = "Third", State = OutputState.Warning },
        };

        _renderer.RenderHistory(entries);

        Assert.Equal(4, _layer._outputLines.Count);
        Assert.Contains("First", _layer._outputLines[0]);
        Assert.Equal("", _layer._outputLines[1]);
        Assert.Contains("Second", _layer._outputLines[2]);
        Assert.Contains("Third", _layer._outputLines[3]);
    }
}