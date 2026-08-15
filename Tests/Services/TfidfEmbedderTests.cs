using ECAssistant.Services;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Services;

public class TfidfEmbedderTests
{
    private readonly TfidfEmbedder _embedder;

    public TfidfEmbedderTests()
    {
        _embedder = new TfidfEmbedder();
    }

    [Fact]
    public void Constructor_Default_CreatesInstance()
    {
        Assert.NotNull(_embedder);
    }

    [Fact]
    public void Embed_ValidText_ReturnsVector()
    {
        var result = _embedder.Embed("hello world");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Embed_ValidText_ReturnsVectorOf128Dimensions()
    {
        var result = _embedder.Embed("test text");
        Assert.Equal(128, result.Length);
    }

    [Fact]
    public void Embed_EmptyString_ReturnsZeroVector()
    {
        var result = _embedder.Embed("");
        Assert.Equal(128, result.Length);
        Assert.All(result, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Embed_WhitespaceOnly_ReturnsZeroVector()
    {
        var result = _embedder.Embed("   ");
        Assert.Equal(128, result.Length);
        Assert.All(result, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Embed_NullText_ReturnsZeroVector()
    {
#pragma warning disable CS8625
        var result = _embedder.Embed(null!);
#pragma warning restore CS8625
        Assert.Equal(128, result.Length);
        Assert.All(result, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Embed_SameText_ReturnsSameVector()
    {
        var result1 = _embedder.Embed("identical text");
        var result2 = _embedder.Embed("identical text");
        Assert.Equal(result1, result2);
    }

    [Fact]
    public void Embed_DifferentText_ReturnsDifferentVector()
    {
        var result1 = _embedder.Embed("first text");
        var result2 = _embedder.Embed("completely different text");
        Assert.NotEqual(result1, result2);
    }

    [Fact]
    public void Embed_NormalizedVector_MagnitudeApproximatelyOne()
    {
        var result = _embedder.Embed("some meaningful text");
        var magnitude = MathF.Sqrt(result.Sum(x => x * x));
        Assert.InRange(magnitude, 0.99f, 1.01f);
    }

    [Fact]
    public void Embed_AllValuesInRange0To1()
    {
        var result = _embedder.Embed("test content here");
        Assert.All(result, v =>
        {
            Assert.True(v >= 0f && v <= 1f, $"Value {v} out of range [0, 1]");
        });
    }

    [Fact]
    public void Embed_LongText_ReturnsValidVector()
    {
        var longText = new string('a', 10000);
        var result = _embedder.Embed(longText);
        Assert.Equal(128, result.Length);
    }
}