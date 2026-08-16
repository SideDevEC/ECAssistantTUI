using ECAssistant.UI;

namespace ECAssistant.Tests.UI;

/// <summary>
/// Unit tests for BaseLayer static methods (ANSI helpers).
/// These don't require a real terminal — all tested methods are static.
/// </summary>
public class BaseLayerAnsiTests
{
    // ── StripAnsi ──

    [Fact]
    public void StripAnsi_PlainText_ReturnsAsIs()
    {
        Assert.Equal("hello world", BaseLayer.StripAnsi("hello world"));
    }

    [Fact]
    public void StripAnsi_RemovesColorCodes()
    {
        string input = "\x1b[31mRed Text\x1b[0m";
        Assert.Equal("Red Text", BaseLayer.StripAnsi(input));
    }

    [Fact]
    public void StripAnsi_RemovesMultipleCodes()
    {
        string input = "\x1b[1m\x1b[32mBold Green\x1b[0m\x1b[0m";
        Assert.Equal("Bold Green", BaseLayer.StripAnsi(input));
    }

    [Fact]
    public void StripAnsi_RemovesCursorPositionCodes()
    {
        string input = "\x1b[10;5HHello";
        Assert.Equal("Hello", BaseLayer.StripAnsi(input));
    }

    [Fact]
    public void StripAnsi_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", BaseLayer.StripAnsi(""));
    }

    [Fact]
    public void StripAnsi_OnlyAnsi_ReturnsEmpty()
    {
        Assert.Equal("", BaseLayer.StripAnsi("\x1b[31m\x1b[0m"));
    }

    [Fact]
    public void StripAnsi_PreservesSpecialChars()
    {
        string input = "\x1b[36m↑ Scrolled up 5 line(s)\x1b[0m";
        Assert.Equal("↑ Scrolled up 5 line(s)", BaseLayer.StripAnsi(input));
    }

    // ── TruncateAnsi ──

    [Fact]
    public void TruncateAnsi_ShortText_ReturnsAsIs()
    {
        string input = "\x1b[31mHi\x1b[0m";
        Assert.Equal(input, BaseLayer.TruncateAnsi(input, 50));
    }

    [Fact]
    public void TruncateAnsi_LongText_TruncatesWithIndicator()
    {
        string input = "\x1b[31mThis is a very long red text that exceeds the limit\x1b[0m";
        var result = BaseLayer.TruncateAnsi(input, 10);
        Assert.Contains("[...]", result);
        Assert.True(BaseLayer.StripAnsi(result).Length <= 10 + 10);
    }

    [Fact]
    public void TruncateAnsi_ExactlyAtLimit_ReturnsAsIs()
    {
        string input = "\x1b[31m12345\x1b[0m";
        Assert.Equal(input, BaseLayer.TruncateAnsi(input, 5));
    }

    [Fact]
    public void TruncateAnsi_PlainText_Truncates()
    {
        string input = "abcdefghij";
        var result = BaseLayer.TruncateAnsi(input, 5);
        Assert.Contains("[...]", result);
    }

    // ── WrapLine ──

    [Fact]
    public void WrapLine_ShortLine_ReturnsSingleEntry()
    {
        var result = BaseLayer.WrapLine("hello", 80);
        Assert.Single(result);
        Assert.Equal("hello", result[0]);
    }

    [Fact]
    public void WrapLine_ExactFit_ReturnsSingleEntry()
    {
        var result = BaseLayer.WrapLine("hello", 5);
        Assert.Single(result);
        Assert.Equal("hello", result[0]);
    }

    [Fact]
    public void WrapLine_LongLine_SplitsIntoMultiple()
    {
        var result = BaseLayer.WrapLine("abcdefghij", 3);
        Assert.Equal(4, result.Count);
        Assert.Equal("abc", result[0]);
        Assert.Equal("def", result[1]);
        Assert.Equal("ghi", result[2]);
        Assert.Equal("j", result[3]);
    }

    [Fact]
    public void WrapLine_EmptyString_ReturnsSingleEmpty()
    {
        var result = BaseLayer.WrapLine("", 80);
        Assert.Single(result);
        Assert.Equal("", result[0]);
    }
}