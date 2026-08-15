using ECAssistant.Services;
using ECAssistant.Interfaces;
using Moq;

namespace ECAssistant.Tests.Services;

public class ContextManagerTests
{
    private readonly Mock<IInferenceEngine> _mockEngine;
    private readonly Mock<IConfigProvider> _mockConfig;

    public ContextManagerTests()
    {
        _mockEngine = new Mock<IInferenceEngine>();
        _mockConfig = new Mock<IConfigProvider>();

        _mockConfig.Setup(c => c.GetInt("context_management.shift_at_messages", 40)).Returns(5);
        _mockConfig.Setup(c => c.GetInt("context_management.keep_last", 20)).Returns(2);
    }

    [Fact]
    public void Constructor_ValidArguments_CreatesInstance()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        Assert.NotNull(mgr);
    }

    [Fact]
    public void Constructor_NullEngine_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ContextManager(null!, _mockConfig.Object));
    }

    [Fact]
    public void Constructor_NullConfigProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ContextManager(_mockEngine.Object, null!));
    }

    [Fact]
    public void MessageCount_EmptyAtStart_ReturnsZero()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        Assert.Equal(0, mgr.MessageCount);
    }

    [Fact]
    public void AddMessage_ValidMessage_IncreasesCount()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        mgr.AddMessage(new TranscriptMessage("user", "hello"));
        Assert.Equal(1, mgr.MessageCount);
    }

    [Fact]
    public void AddMessage_MultipleMessages_IncreasesCountCorrectly()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        mgr.AddMessage(new TranscriptMessage("user", "msg1"));
        mgr.AddMessage(new TranscriptMessage("assistant", "msg2"));
        mgr.AddMessage(new TranscriptMessage("user", "msg3"));
        Assert.Equal(3, mgr.MessageCount);
    }

    [Fact]
    public void GetMessages_ReturnsCopyOfMessages()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        mgr.AddMessage(new TranscriptMessage("user", "hello"));
        var messages = mgr.GetMessages();
        mgr.AddMessage(new TranscriptMessage("user", "world"));
        // Original list should not be modified by adding to the copy
        Assert.Single(messages);
        Assert.Equal(2, mgr.MessageCount);
    }

    [Fact]
    public void GetMessages_Empty_ReturnsEmptyList()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        var messages = mgr.GetMessages();
        Assert.Empty(messages);
    }

    [Fact]
    public void GetMessages_ReturnsAllAddedMessages()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        mgr.AddMessage(new TranscriptMessage("user", "first"));
        mgr.AddMessage(new TranscriptMessage("assistant", "second"));
        var messages = mgr.GetMessages();
        Assert.Equal(2, messages.Count);
        Assert.Equal("first", messages[0].Content);
        Assert.Equal("second", messages[1].Content);
    }

    [Fact]
    public void NeedsShift_BelowThreshold_ReturnsFalse()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        for (int i = 0; i < 4; i++)
        {
            mgr.AddMessage(new TranscriptMessage("user", $"msg{i}"));
        }
        Assert.False(mgr.NeedsShift());
    }

    [Fact]
    public void NeedsShift_AtThreshold_ReturnsTrue()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        for (int i = 0; i < 5; i++)
        {
            mgr.AddMessage(new TranscriptMessage("user", $"msg{i}"));
        }
        Assert.True(mgr.NeedsShift());
    }

    [Fact]
    public void NeedsShift_AboveThreshold_ReturnsTrue()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        for (int i = 0; i < 10; i++)
        {
            mgr.AddMessage(new TranscriptMessage("user", $"msg{i}"));
        }
        Assert.True(mgr.NeedsShift());
    }

    [Fact]
    public void Shift_MoreMessagesThanKeepLast_KeepsOnlyLastN()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        for (int i = 0; i < 10; i++)
        {
            mgr.AddMessage(new TranscriptMessage("user", $"msg{i}"));
        }
        mgr.Shift();
        Assert.Equal(2, mgr.MessageCount);
    }

    [Fact]
    public void Shift_FewerMessagesThanKeepLast_DoesNotShift()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        mgr.AddMessage(new TranscriptMessage("user", "msg1"));
        mgr.AddMessage(new TranscriptMessage("user", "msg2"));
        mgr.Shift();
        Assert.Equal(2, mgr.MessageCount);
    }

    [Fact]
    public void Shift_KeepsCorrectMessages()
    {
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        for (int i = 0; i < 10; i++)
        {
            mgr.AddMessage(new TranscriptMessage("user", $"msg{i}"));
        }
        mgr.Shift();
        var messages = mgr.GetMessages();
        Assert.Equal("msg8", messages[0].Content);
        Assert.Equal("msg9", messages[1].Content);
    }

    [Fact]
    public async Task SummarizeAsync_CallsInferenceEngine()
    {
        _mockEngine.Setup(e => e.GenerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("summary result");
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        mgr.AddMessage(new TranscriptMessage("user", "hello"));
        mgr.AddMessage(new TranscriptMessage("assistant", "hi there"));
        var result = await mgr.SummarizeAsync();
        Assert.Equal("summary result", result);
        _mockEngine.Verify(e => e.GenerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SummarizeAsync_IncludesAllMessagesInPrompt()
    {
        _mockEngine.Setup(e => e.GenerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("summary");
        var mgr = new ContextManager(_mockEngine.Object, _mockConfig.Object);
        mgr.AddMessage(new TranscriptMessage("user", "important question"));
        mgr.AddMessage(new TranscriptMessage("assistant", "important answer"));
        await mgr.SummarizeAsync();
        _mockEngine.Verify(e => e.GenerateAsync(
            It.Is<string>(s => s.Contains("important question") && s.Contains("important answer")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Constructor_ReadsShiftAtFromConfig()
    {
        var mockConfig = new Mock<IConfigProvider>();
        mockConfig.Setup(c => c.GetInt("context_management.shift_at_messages", 40)).Returns(20);
        mockConfig.Setup(c => c.GetInt("context_management.keep_last", 20)).Returns(10);
        var mgr = new ContextManager(_mockEngine.Object, mockConfig.Object);
        for (int i = 0; i < 19; i++)
        {
            mgr.AddMessage(new TranscriptMessage("user", $"msg{i}"));
        }
        Assert.False(mgr.NeedsShift());
        mgr.AddMessage(new TranscriptMessage("user", "msg20"));
        Assert.True(mgr.NeedsShift());
    }
}