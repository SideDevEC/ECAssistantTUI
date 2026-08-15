using ECAssistant.UI;

namespace ECAssistant.Tests.UI;

/// <summary>
/// Tests for EGuiConsole output buffer management (AddOutputLine, _outputLines).
/// Uses InternalsVisibleTo to access internal members.
/// </summary>
public class EGuiConsoleBufferTests
{
    /// <summary>
    /// Create an EGuiConsole without calling InitConsole (which enters alternate buffer).
    /// Output goes to _startupBuffer until InitConsole is called.
    /// We call InitConsole with no ANSI to flush, then test AddOutputLine directly.
    /// </summary>
    private static EGuiConsole CreateConsole()
    {
        var console = new EGuiConsole();
        // Don't call InitConsole — just use AddOutputLine directly
        return console;
    }

    [Fact]
    public void AddOutputLine_SingleLine_AddsOneEntry()
    {
        var console = CreateConsole();
        console.AddOutputLine("hello world");
        Assert.Single(console._outputLines);
        Assert.Equal("hello world", console._outputLines[0]);
    }

    [Fact]
    public void AddOutputLine_MultiLine_SplitsCorrectly()
    {
        var console = CreateConsole();
        console.AddOutputLine("line1\nline2\nline3");
        Assert.Equal(3, console._outputLines.Count);
        Assert.Equal("line1", console._outputLines[0]);
        Assert.Equal("line2", console._outputLines[1]);
        Assert.Equal("line3", console._outputLines[2]);
    }

    [Fact]
    public void AddOutputLine_TrailingNewline_DoesNotCreateEmptyLine()
    {
        var console = CreateConsole();
        console.AddOutputLine("text\n");
        Assert.Single(console._outputLines);
        Assert.Equal("text", console._outputLines[0]);
    }

    [Fact]
    public void AddOutputLine_MultipleLinesWithTrailingNewline_NoEmptyEntry()
    {
        var console = CreateConsole();
        console.AddOutputLine("a\nb\nc\n");
        Assert.Equal(3, console._outputLines.Count);
        Assert.Equal("a", console._outputLines[0]);
        Assert.Equal("b", console._outputLines[1]);
        Assert.Equal("c", console._outputLines[2]);
    }

    [Fact]
    public void AddOutputLine_EmptyString_AddsEmptyEntry()
    {
        var console = CreateConsole();
        console.AddOutputLine("");
        Assert.Single(console._outputLines);
        Assert.Equal("", console._outputLines[0]);
    }

    [Fact]
    public void AddOutputLine_WindowsLineEndings_StripsCarriageReturn()
    {
        var console = CreateConsole();
        console.AddOutputLine("hello\r\nworld\r\n");
        Assert.Equal(2, console._outputLines.Count);
        Assert.Equal("hello", console._outputLines[0]);
        Assert.Equal("world", console._outputLines[1]);
    }

    [Fact]
    public void AddOutputLine_MultipleCalls_AppendsInOrder()
    {
        var console = CreateConsole();
        console.AddOutputLine("first\n");
        console.AddOutputLine("second\n");
        console.AddOutputLine("third\n");
        Assert.Equal(3, console._outputLines.Count);
        Assert.Equal("first", console._outputLines[0]);
        Assert.Equal("second", console._outputLines[1]);
        Assert.Equal("third", console._outputLines[2]);
    }

    [Fact]
    public void AddOutputLine_BlankLines_Preserved()
    {
        var console = CreateConsole();
        console.AddOutputLine("text1\n\n\ntext2\n");
        Assert.Equal(4, console._outputLines.Count);
        Assert.Equal("text1", console._outputLines[0]);
        Assert.Equal("", console._outputLines[1]);
        Assert.Equal("", console._outputLines[2]);
        Assert.Equal("text2", console._outputLines[3]);
    }
}