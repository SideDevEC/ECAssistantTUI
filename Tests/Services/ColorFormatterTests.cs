using ECAssistant.Services;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Services;

public class ColorFormatterTests
{
    [Fact]
    public void Constructor_Default_CreatesInstance()
    {
        var formatter = new ColorFormatter();
        Assert.NotNull(formatter);
    }

    [Theory]
    [InlineData("red", "\x1b[31m")]
    [InlineData("green", "\x1b[32m")]
    [InlineData("blue", "\x1b[34m")]
    [InlineData("yellow", "\x1b[33m")]
    [InlineData("cyan", "\x1b[36m")]
    [InlineData("white", "\x1b[37m")]
    public void Format_KnownColor_WrapsTextWithColorCode(string color, string expectedCode)
    {
        var formatter = new ColorFormatter();
        var result = formatter.Format(color, "hello");
        Assert.StartsWith(expectedCode, result);
        Assert.EndsWith("\x1b[0m", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public void Format_UnknownColor_WrapsWithReset()
    {
        var formatter = new ColorFormatter();
        var result = formatter.Format("unknown", "text");
        Assert.StartsWith("\x1b[0m", result);
        Assert.Contains("text", result);
    }

    [Fact]
    public void Format_EmptyText_ReturnsColorAndReset()
    {
        var formatter = new ColorFormatter();
        var result = formatter.Format("red", "");
        Assert.StartsWith("\x1b[31m", result);
        Assert.EndsWith("\x1b[0m", result);
    }

    [Fact]
    public void Reset_ReturnsAnsiResetCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[0m", formatter.Reset);
    }

    [Fact]
    public void Bold_ReturnsAnsiBoldCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[1m", formatter.Bold);
    }

    [Fact]
    public void Dim_ReturnsAnsiDimCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[2m", formatter.Dim);
    }

    [Fact]
    public void Black_ReturnsAnsiBlackCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[30m", formatter.Black);
    }

    [Fact]
    public void Red_ReturnsAnsiRedCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[31m", formatter.Red);
    }

    [Fact]
    public void Green_ReturnsAnsiGreenCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[32m", formatter.Green);
    }

    [Fact]
    public void Blue_ReturnsAnsiBlueCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[34m", formatter.Blue);
    }

    [Fact]
    public void Yellow_ReturnsAnsiYellowCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[33m", formatter.Yellow);
    }

    [Fact]
    public void Cyan_ReturnsAnsiCyanCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[36m", formatter.Cyan);
    }

    [Fact]
    public void White_ReturnsAnsiWhiteCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[37m", formatter.White);
    }

    [Fact]
    public void Magenta_ReturnsAnsiMagentaCode()
    {
        var formatter = new ColorFormatter();
        Assert.Equal("\x1b[35m", formatter.Magenta);
    }

    [Fact]
    public void Format_WithReset_AlwaysAppendsReset()
    {
        var formatter = new ColorFormatter();
        var result = formatter.Format("red", "test");
        Assert.EndsWith("\x1b[0m", result);
    }

    [Fact]
    public void WriteLine_DoesNotThrow()
    {
        var formatter = new ColorFormatter();
        // Redirect console to avoid noise
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            formatter.WriteLine("red", "test message");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        Assert.Contains("test message", sw.ToString());
    }

    [Fact]
    public void Write_DoesNotThrow()
    {
        var formatter = new ColorFormatter();
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            formatter.Write("green", "inline text");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        Assert.Contains("inline text", sw.ToString());
    }

    [Fact]
    public void Tag_DoesNotThrow()
    {
        var formatter = new ColorFormatter();
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            formatter.Tag("cyan", "LABEL", "message body");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        var output = sw.ToString();
        Assert.Contains("[LABEL]", output);
        Assert.Contains("message body", output);
    }

    [Fact]
    public void TagBold_DoesNotThrow()
    {
        var formatter = new ColorFormatter();
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            formatter.TagBold("red", "ERROR", "something failed");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        var output = sw.ToString();
        Assert.Contains("[ERROR]", output);
        Assert.Contains("something failed", output);
    }
}