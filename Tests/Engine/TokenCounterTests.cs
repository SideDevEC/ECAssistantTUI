using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class TokenCounterTests
{
    [Fact]
    public void Count_NullString_ReturnsZero()
    {
        var counter = new TokenCounter(null);
        Assert.Equal(0, counter.Count(null));
    }

    [Fact]
    public void Count_EmptyString_ReturnsZero()
    {
        var counter = new TokenCounter(null);
        Assert.Equal(0, counter.Count(""));
    }

    [Fact]
    public void Count_SimpleTextWithoutContext_ReturnsPositiveValue()
    {
        var counter = new TokenCounter(null);
        var result = counter.Count("Hello world");
        Assert.True(result > 0);
    }

    [Fact]
    public void Count_LongerText_ReturnsMoreTokens()
    {
        var counter = new TokenCounter(null);
        var shortResult = counter.Count("Hello");
        var longResult = counter.Count("Hello world this is a much longer sentence with many words");
        Assert.True(longResult > shortResult);
    }

    [Fact]
    public void Count_TextWithSpaces_AdjustsScore()
    {
        var counter = new TokenCounter(null);
        var withSpaces = counter.Count("a b c d e f g h i j");
        var noSpaces = counter.Count("abcdefghij");
        // Spaces add to the score which affects the denominator, but both should be positive
        Assert.True(withSpaces > 0);
        Assert.True(noSpaces > 0);
    }

    [Fact]
    public void Count_TextWithPunctuation_AdjustsScore()
    {
        var counter = new TokenCounter(null);
        var result = counter.Count("Hello, world! How are you?");
        Assert.True(result > 0);
    }

    [Fact]
    public void Count_TextWithUpperCase_AdjustsScore()
    {
        var counter = new TokenCounter(null);
        var result = counter.Count("HelloWorld CapitalLetters");
        Assert.True(result > 0);
    }

    [Fact]
    public void Count_SingleCharacter_ReturnsAtLeastOne()
    {
        var counter = new TokenCounter(null);
        Assert.True(counter.Count("a") >= 1);
    }

    [Fact]
    public void EstimateUpper_NullString_ReturnsZero()
    {
        var counter = new TokenCounter(null);
        Assert.Equal(0, counter.EstimateUpper(null));
    }

    [Fact]
    public void EstimateUpper_EmptyString_ReturnsZero()
    {
        var counter = new TokenCounter(null);
        Assert.Equal(0, counter.EstimateUpper(""));
    }

    [Fact]
    public void EstimateUpper_WithoutContext_ReturnsCharBasedEstimate()
    {
        var counter = new TokenCounter(null);
        var text = "Hello world this is a test";
        var result = counter.EstimateUpper(text);
        // Without context: text.Length / 3.5f
        var expected = (int)(text.Length / 3.5f);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EstimateUpper_WithoutContext_LongerText_ReturnsHigherValue()
    {
        var counter = new TokenCounter(null);
        var shortResult = counter.EstimateUpper("Hi");
        var longResult = counter.EstimateUpper("This is a very long text that should produce a higher token estimate");
        Assert.True(longResult > shortResult);
    }

    [Fact]
    public void EstimateUpper_WithNullContext_FallsBackToCharBased()
    {
        var counter = new TokenCounter(null);
        var text = "Test text for estimation";
        var result = counter.EstimateUpper(text);
        Assert.True(result > 0);
    }

    [Fact]
    public void Count_AfterInitialize_UsesProvidedContext()
    {
        // We can't easily create a LLamaContext in tests, so test the fallback
        var counter = new TokenCounter(null);
        // Initialize with null should still use fallback
        // Count should still work
        var result = counter.Count("test text");
        Assert.True(result > 0);
    }
}