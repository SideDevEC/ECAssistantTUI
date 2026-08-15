using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class FailurePatternTests
{
    [Theory]
    [InlineData(FailurePattern.None)]
    [InlineData(FailurePattern.Isolated)]
    [InlineData(FailurePattern.RepeatedError)]
    [InlineData(FailurePattern.ToolLoop)]
    [InlineData(FailurePattern.Alternating)]
    public void AllEnumValues_Defined(FailurePattern pattern)
    {
        Assert.True(Enum.IsDefined(typeof(FailurePattern), pattern));
    }

    [Fact]
    public void Enum_HasFiveValues()
    {
        var values = Enum.GetValues<FailurePattern>();
        Assert.Equal(5, values.Length);
    }

    [Fact]
    public void None_IsFirstValue()
    {
        var values = Enum.GetValues<FailurePattern>();
        Assert.Equal(FailurePattern.None, values[0]);
    }

    [Fact]
    public void None_HasDefaultValueZero()
    {
        Assert.Equal(0, (int)FailurePattern.None);
    }

    [Theory]
    [InlineData("None", FailurePattern.None)]
    [InlineData("Isolated", FailurePattern.Isolated)]
    [InlineData("RepeatedError", FailurePattern.RepeatedError)]
    [InlineData("ToolLoop", FailurePattern.ToolLoop)]
    [InlineData("Alternating", FailurePattern.Alternating)]
    public void Parse_ValidString_ReturnsCorrectValue(string name, FailurePattern expected)
    {
        var parsed = Enum.Parse<FailurePattern>(name);
        Assert.Equal(expected, parsed);
    }
}