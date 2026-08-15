using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class FailureAnalysisTests
{
    [Fact]
    public void DefaultValues_AllPropertiesHaveCorrectDefaults()
    {
        var analysis = new FailureAnalysis();
        Assert.Equal(FailurePattern.None, analysis.Pattern);
        Assert.Equal("", analysis.Recommendation);
        Assert.False(analysis.ShouldEscalate);
    }

    [Fact]
    public void Pattern_SetValue_ReturnsSameValue()
    {
        var analysis = new FailureAnalysis { Pattern = FailurePattern.RepeatedError };
        Assert.Equal(FailurePattern.RepeatedError, analysis.Pattern);
    }

    [Fact]
    public void Recommendation_SetValue_ReturnsSameValue()
    {
        var analysis = new FailureAnalysis { Recommendation = "Try a different approach" };
        Assert.Equal("Try a different approach", analysis.Recommendation);
    }

    [Fact]
    public void ShouldEscalate_SetTrue_ReturnsTrue()
    {
        var analysis = new FailureAnalysis { ShouldEscalate = true };
        Assert.True(analysis.ShouldEscalate);
    }

    [Fact]
    public void ShouldEscalate_SetFalse_ReturnsFalse()
    {
        var analysis = new FailureAnalysis { ShouldEscalate = false };
        Assert.False(analysis.ShouldEscalate);
    }

    [Theory]
    [InlineData(FailurePattern.None)]
    [InlineData(FailurePattern.Isolated)]
    [InlineData(FailurePattern.RepeatedError)]
    [InlineData(FailurePattern.ToolLoop)]
    [InlineData(FailurePattern.Alternating)]
    public void Pattern_AllEnumValues_CanBeSet(FailurePattern pattern)
    {
        var analysis = new FailureAnalysis { Pattern = pattern };
        Assert.Equal(pattern, analysis.Pattern);
    }
}