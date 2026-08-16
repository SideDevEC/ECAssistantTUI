using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Tests.UI;

/// <summary>
/// Tests for BaseLayer output buffer management (AddOutputLine, _outputLines).
/// Uses InternalsVisibleTo to access internal members.
/// </summary>
public class BaseLayerBufferTests
{
    /// <summary>
    /// Create a SessionLayer to test BaseLayer buffer methods.
    /// SessionLayer is concrete and simple — perfect for testing base behavior.
    /// </summary>
    private static SessionLayer CreateLayer()
    {
        return new SessionLayer("test");
    }

    [Fact]
    public void AddOutputLine_SingleLine_AddsOneEntry()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("hello world");
        Assert.Single(layer._outputLines);
        Assert.Equal("hello world", layer._outputLines[0]);
    }

    [Fact]
    public void AddOutputLine_MultiLine_SplitsCorrectly()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("line1\nline2\nline3");
        Assert.Equal(3, layer._outputLines.Count);
        Assert.Equal("line1", layer._outputLines[0]);
        Assert.Equal("line2", layer._outputLines[1]);
        Assert.Equal("line3", layer._outputLines[2]);
    }

    [Fact]
    public void AddOutputLine_TrailingNewline_DoesNotCreateEmptyLine()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("text\n");
        Assert.Single(layer._outputLines);
        Assert.Equal("text", layer._outputLines[0]);
    }

    [Fact]
    public void AddOutputLine_MultipleLinesWithTrailingNewline_NoEmptyEntry()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("a\nb\nc\n");
        Assert.Equal(3, layer._outputLines.Count);
        Assert.Equal("a", layer._outputLines[0]);
        Assert.Equal("b", layer._outputLines[1]);
        Assert.Equal("c", layer._outputLines[2]);
    }

    [Fact]
    public void AddOutputLine_EmptyString_AddsEmptyEntry()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("");
        Assert.Single(layer._outputLines);
        Assert.Equal("", layer._outputLines[0]);
    }

    [Fact]
    public void AddOutputLine_WindowsLineEndings_StripsCarriageReturn()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("hello\r\nworld\r\n");
        Assert.Equal(2, layer._outputLines.Count);
        Assert.Equal("hello", layer._outputLines[0]);
        Assert.Equal("world", layer._outputLines[1]);
    }

    [Fact]
    public void AddOutputLine_MultipleCalls_AppendsInOrder()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("first\n");
        layer.AddOutputLine("second\n");
        layer.AddOutputLine("third\n");
        Assert.Equal(3, layer._outputLines.Count);
        Assert.Equal("first", layer._outputLines[0]);
        Assert.Equal("second", layer._outputLines[1]);
        Assert.Equal("third", layer._outputLines[2]);
    }

    [Fact]
    public void AddOutputLine_BlankLines_Preserved()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("text1\n\n\ntext2\n");
        Assert.Equal(4, layer._outputLines.Count);
        Assert.Equal("text1", layer._outputLines[0]);
        Assert.Equal("", layer._outputLines[1]);
        Assert.Equal("", layer._outputLines[2]);
        Assert.Equal("text2", layer._outputLines[3]);
    }

    [Fact]
    public void AddOutputLine_ResetsScrollToBottom()
    {
        var layer = CreateLayer();
        // Add enough lines so scroll is actually possible (need more than region height)
        for (int i = 0; i < 30; i++)
            layer.AddOutputLine($"line {i}");
        layer.ScrollUp(5);
        Assert.True(layer.ScrollOffset > 0);
        layer.AddOutputLine("new line");
        Assert.Equal(0, layer.ScrollOffset);
    }

    [Fact]
    public void Clear_RemovesAllLines()
    {
        var layer = CreateLayer();
        layer.AddOutputLine("line1");
        layer.AddOutputLine("line2");
        Assert.Equal(2, layer._outputLines.Count);
        layer.Clear();
        Assert.Empty(layer._outputLines);
    }
}