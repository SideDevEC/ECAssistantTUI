using ECAssistant.Interfaces;
using ECAssistant.Tools;

namespace ECAssistant.Tests.Tools;

public class ToolAdapterTests
{
    [Fact]
    public void Name_DelegatesToInnerTool()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Name).Returns("InnerTool");

        var adapter = new ToolAdapter(mockTool.Object);

        Assert.Equal("InnerTool", adapter.Name);
    }

    [Fact]
    public void Description_DelegatesToInnerTool()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Description).Returns("Inner description");

        var adapter = new ToolAdapter(mockTool.Object);

        Assert.Equal("Inner description", adapter.Description);
    }

    [Fact]
    public void Inner_Property_ReturnsWrappedTool()
    {
        var mockTool = new Mock<ITool>();

        var adapter = new ToolAdapter(mockTool.Object);

        Assert.Same(mockTool.Object, adapter.Inner);
    }

    [Fact]
    public void UsageExample_AlwaysReturnsEmpty()
    {
        var mockTool = new Mock<ITool>();
        var adapter = new ToolAdapter(mockTool.Object);

        Assert.Equal(string.Empty, adapter.UsageExample);
    }

    [Fact]
    public void GetExtendedSystemPrompt_ReturnsInnerDescription()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Description).Returns("Extended prompt text");

        var adapter = new ToolAdapter(mockTool.Object);

        Assert.Equal("Extended prompt text", adapter.GetExtendedSystemPrompt());
    }

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsSuccessResult()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Name).Returns("InnerTool");
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("operation completed");

        var adapter = new ToolAdapter(mockTool.Object);
        var args = new Dictionary<string, string?> { ["key"] = "value" };

        var result = await adapter.ExecuteAsync(args);

        Assert.True(result.Succeeded);
        Assert.Equal("InnerTool", result.ToolName);
        Assert.Equal("operation completed", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_FailedResult_ReturnsFailureResult()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Name).Returns("InnerTool");
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("[FAILED] something went wrong");

        var adapter = new ToolAdapter(mockTool.Object);

        var result = await adapter.ExecuteAsync(new Dictionary<string, string?>());

        Assert.False(result.Succeeded);
        Assert.Equal("InnerTool", result.ToolName);
        Assert.Contains("something went wrong", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArgs_PassesEmptyString()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Name).Returns("T");
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ok");

        var adapter = new ToolAdapter(mockTool.Object);

        await adapter.ExecuteAsync(new Dictionary<string, string?>());

        mockTool.Verify(t => t.ExecuteAsync("", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NullArgs_PassesEmptyString()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Name).Returns("T");
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ok");

        var adapter = new ToolAdapter(mockTool.Object);

        await adapter.ExecuteAsync(null!);

        mockTool.Verify(t => t.ExecuteAsync("", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ArgWithNullValue_AppendsKeyOnly()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Name).Returns("T");
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ok");

        var adapter = new ToolAdapter(mockTool.Object);
        var args = new Dictionary<string, string?> { ["key"] = null };

        await adapter.ExecuteAsync(args);

        // null value means only the key is appended (with trailing space), then Trim() removes it
        mockTool.Verify(t => t.ExecuteAsync("key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleArgs_FormatsCorrectly()
    {
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Name).Returns("T");
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ok");

        var adapter = new ToolAdapter(mockTool.Object);
        var args = new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" };

        await adapter.ExecuteAsync(args);

        mockTool.Verify(t => t.ExecuteAsync(It.Is<string>(s => s.Contains("a=\"1\"") && s.Contains("b=\"2\"")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCancellationToken()
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var mockTool = new Mock<ITool>();
        mockTool.SetupGet(t => t.Name).Returns("T");
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), token))
                .ReturnsAsync("ok");

        var adapter = new ToolAdapter(mockTool.Object);

        await adapter.ExecuteAsync(new Dictionary<string, string?>(), token);

        mockTool.Verify(t => t.ExecuteAsync(It.IsAny<string>(), token), Times.Once);
    }
}