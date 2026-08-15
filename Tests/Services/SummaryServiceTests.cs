using ECAssistant.Services;
using ECAssistant.Engine;

namespace ECAssistant.Tests.Services;

public class SummaryServiceTests
{
    [Fact]
    public void Constructor_WithNullGenerator_CreatesInstance()
    {
        var service = new SummaryService(null);
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_WithGenerator_CreatesInstance()
    {
        Func<string, Task<string>> gen = _ => Task.FromResult("summary");
        var service = new SummaryService(gen);
        Assert.NotNull(service);
    }

    [Fact]
    public async Task SummarizeAsync_NullMessages_ReturnsNoMessagesMessage()
    {
        var service = new SummaryService(null);
        var result = await service.SummarizeAsync(null!);
        Assert.Contains("No messages to summarize", result);
    }

    [Fact]
    public async Task SummarizeAsync_EmptyMessages_ReturnsNoMessagesMessage()
    {
        var service = new SummaryService(null);
        var result = await service.SummarizeAsync(new List<TranscriptMessage>());
        Assert.Contains("No messages to summarize", result);
    }

    [Fact]
    public async Task SummarizeAsync_WithNullGenerator_UsesExtractiveSummary()
    {
        var service = new SummaryService(null);
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User("Hello, how are you?"),
            TranscriptMessage.Assistant("I'm doing well, thank you!"),
            TranscriptMessage.User("Can you help me with a task?")
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("extractive", result);
        Assert.Contains("Hello, how are you?", result);
    }

    [Fact]
    public async Task SummarizeAsync_WithGenerator_UsesLLMSummary()
    {
        Func<string, Task<string>> gen = prompt => Task.FromResult("This is a concise LLM summary.");
        var service = new SummaryService(gen);
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User("Tell me about the project."),
            TranscriptMessage.Assistant("It's a great project.")
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("This is a concise LLM summary.", result);
        Assert.Contains("[Summary of 2 messages]", result);
    }

    [Fact]
    public async Task SummarizeAsync_LLMReturnsEmpty_FallsBackToExtractive()
    {
        Func<string, Task<string>> gen = _ => Task.FromResult("");
        var service = new SummaryService(gen);
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User("test message")
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("extractive", result);
        Assert.Contains("LLM returned empty", result);
    }

    [Fact]
    public async Task SummarizeAsync_LLMReturnsWhitespace_FallsBackToExtractive()
    {
        Func<string, Task<string>> gen = _ => Task.FromResult("   ");
        var service = new SummaryService(gen);
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User("test message")
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("extractive", result);
    }

    [Fact]
    public async Task SummarizeAsync_LLMThrows_FallsBackToExtractiveWithError()
    {
        Func<string, Task<string>> gen = _ => throw new InvalidOperationException("LLM unavailable");
        var service = new SummaryService(gen);
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User("test message")
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("extractive", result);
        Assert.Contains("LLM error", result);
        Assert.Contains("LLM unavailable", result);
    }

    [Fact]
    public async Task SummarizeAsync_WithSourceInMessage_IncludesSourceInExtractive()
    {
        var service = new SummaryService(null);
        var messages = new List<TranscriptMessage>
        {
            new() { Role = "user", Source = "user", Content = "message with source" }
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("user", result);
        Assert.Contains("message with source", result);
    }

    [Fact]
    public async Task SummarizeAsync_LongMessage_TruncatesInExtractiveSummary()
    {
        var service = new SummaryService(null);
        var longContent = new string('x', 300);
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User(longContent)
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("...", result);
    }

    [Fact]
    public async Task SummarizeAsync_LLMSummary_EscapesAngleBrackets()
    {
        Func<string, Task<string>> gen = _ => Task.FromResult("Summary with <script> tags");
        var service = new SummaryService(gen);
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User("test")
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("&lt;script&gt;", result);
        Assert.DoesNotContain("<script>", result);
    }

    [Fact]
    public async Task SummarizeAsync_MultipleMessages_ReportsCorrectCount()
    {
        var service = new SummaryService(null);
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User("msg1"),
            TranscriptMessage.User("msg2"),
            TranscriptMessage.User("msg3"),
            TranscriptMessage.User("msg4"),
            TranscriptMessage.User("msg5")
        };
        var result = await service.SummarizeAsync(messages);
        Assert.Contains("[Summary of 5 messages", result);
    }
}