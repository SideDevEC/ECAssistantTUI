using System.Text.Json;
using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class ConversationTranscriptTests
{
    [Fact]
    public void TranscriptMessage_System_SetsRoleAndContent()
    {
        var msg = TranscriptMessage.System("System prompt");
        Assert.Equal("system", msg.Role);
        Assert.Equal("System prompt", msg.Content);
    }

    [Fact]
    public void TranscriptMessage_System_DefaultSource()
    {
        var msg = TranscriptMessage.System("test");
        Assert.Equal("", msg.Source);
    }

    [Fact]
    public void TranscriptMessage_User_SetsRoleAndContent()
    {
        var msg = TranscriptMessage.User("Hello");
        Assert.Equal("user", msg.Role);
        Assert.Equal("Hello", msg.Content);
    }

    [Fact]
    public void TranscriptMessage_User_DefaultSource()
    {
        var msg = TranscriptMessage.User("Hello");
        Assert.Equal("user", msg.Source);
    }

    [Fact]
    public void TranscriptMessage_User_CustomSource()
    {
        var msg = TranscriptMessage.User("Hello", "custom");
        Assert.Equal("custom", msg.Source);
    }

    [Fact]
    public void TranscriptMessage_Assistant_SetsRoleAndContent()
    {
        var msg = TranscriptMessage.Assistant("I can help");
        Assert.Equal("assistant", msg.Role);
        Assert.Equal("I can help", msg.Content);
    }

    [Fact]
    public void TranscriptMessage_Assistant_DefaultSource()
    {
        var msg = TranscriptMessage.Assistant("test");
        Assert.Equal("assistant", msg.Source);
    }

    [Fact]
    public void TranscriptMessage_Assistant_CustomSource()
    {
        var msg = TranscriptMessage.Assistant("test", "model-xyz");
        Assert.Equal("model-xyz", msg.Source);
    }

    [Fact]
    public void TranscriptMessage_ToolOutput_SetsRoleAndSource()
    {
        var msg = TranscriptMessage.ToolOutput("output", "EShellAgent");
        Assert.Equal("tool_output", msg.Role);
        Assert.Equal("EShellAgent", msg.Source);
        Assert.Equal("output", msg.Content);
    }

    [Fact]
    public void TranscriptMessage_ToolOutput_EmptyToolName()
    {
        var msg = TranscriptMessage.ToolOutput("output");
        Assert.Equal("tool_output", msg.Role);
        Assert.Equal("", msg.Source);
    }

    [Fact]
    public void TranscriptMessage_ToJson_ReturnsValidJson()
    {
        var msg = TranscriptMessage.User("Hello world");
        var json = msg.ToJson();
        Assert.Contains("\"Role\":\"user\"", json);
        Assert.Contains("\"Content\":\"Hello world\"", json);
    }

    [Fact]
    public void TranscriptMessage_FromJson_RoundTripsCorrectly()
    {
        var original = TranscriptMessage.Assistant("Test response");
        original.EstimatedTokens = 42;
        var json = original.ToJson();
        var restored = TranscriptMessage.FromJson(json);
        Assert.Equal("assistant", restored.Role);
        Assert.Equal("Test response", restored.Content);
        Assert.Equal(42, restored.EstimatedTokens);
    }

    [Fact]
    public void TranscriptMessage_FromJson_WithToolOutput()
    {
        var original = TranscriptMessage.ToolOutput("result", "EShellAgent");
        var json = original.ToJson();
        var restored = TranscriptMessage.FromJson(json);
        Assert.Equal("tool_output", restored.Role);
        Assert.Equal("EShellAgent", restored.Source);
    }

    [Fact]
    public void ConversationTranscript_Default_HasEmptyMessages()
    {
        var transcript = new ConversationTranscript();
        Assert.Empty(transcript.Messages);
        Assert.Equal(0, transcript.MessageCount);
        Assert.Equal(0, transcript.TotalTokens);
    }

    [Fact]
    public void ConversationTranscript_AddUser_AddsUserMessage()
    {
        var transcript = new ConversationTranscript();
        transcript.AddUser("Hello");
        Assert.Single(transcript.Messages);
        Assert.Equal("user", transcript.Messages[0].Role);
        Assert.Equal("Hello", transcript.Messages[0].Content);
    }

    [Fact]
    public void ConversationTranscript_AddAssistant_AddsAssistantMessage()
    {
        var transcript = new ConversationTranscript();
        transcript.AddAssistant("Hi");
        Assert.Single(transcript.Messages);
        Assert.Equal("assistant", transcript.Messages[0].Role);
    }

    [Fact]
    public void ConversationTranscript_AddToolOutput_AddsToolOutputMessage()
    {
        var transcript = new ConversationTranscript();
        transcript.AddToolOutput("output", "EShellAgent");
        Assert.Single(transcript.Messages);
        Assert.Equal("tool_output", transcript.Messages[0].Role);
        Assert.Equal("EShellAgent", transcript.Messages[0].Source);
    }

    [Fact]
    public void ConversationTranscript_AddSystem_InsertsAtBeginning()
    {
        var transcript = new ConversationTranscript();
        transcript.AddUser("Hello");
        transcript.AddSystem("System prompt");
        Assert.Equal(2, transcript.Messages.Count);
        Assert.Equal("system", transcript.Messages[0].Role);
    }

    [Fact]
    public void ConversationTranscript_Add_AddsMessageToList()
    {
        var transcript = new ConversationTranscript();
        transcript.Add(TranscriptMessage.User("test"));
        Assert.Single(transcript.Messages);
    }

    [Fact]
    public void ConversationTranscript_AddRange_AddsAllMessages()
    {
        var transcript = new ConversationTranscript();
        var msgs = new List<TranscriptMessage>
        {
            TranscriptMessage.User("a"),
            TranscriptMessage.User("b"),
            TranscriptMessage.User("c")
        };
        transcript.AddRange(msgs);
        Assert.Equal(3, transcript.Messages.Count);
    }

    [Fact]
    public void ConversationTranscript_MessageCount_ReturnsCorrectCount()
    {
        var transcript = new ConversationTranscript();
        transcript.AddUser("a");
        transcript.AddAssistant("b");
        Assert.Equal(2, transcript.MessageCount);
    }

    [Fact]
    public void ConversationTranscript_TotalTokens_SumsAllTokens()
    {
        var transcript = new ConversationTranscript();
        var msg1 = TranscriptMessage.User("a");
        msg1.EstimatedTokens = 10;
        var msg2 = TranscriptMessage.Assistant("b");
        msg2.EstimatedTokens = 20;
        transcript.Add(msg1);
        transcript.Add(msg2);
        Assert.Equal(30, transcript.TotalTokens);
    }

    [Fact]
    public void ConversationTranscript_ToJson_ReturnsValidJson()
    {
        var transcript = new ConversationTranscript();
        transcript.AddUser("Hello");
        var json = transcript.ToJson();
        Assert.Contains("\"Messages\"", json);
        Assert.Contains("Hello", json);
    }

    [Fact]
    public void ConversationTranscript_FromJson_RoundTripsCorrectly()
    {
        var transcript = new ConversationTranscript();
        transcript.AddUser("Hello");
        transcript.AddAssistant("Hi");
        var json = transcript.ToJson();
        var restored = ConversationTranscript.FromJson(json);
        Assert.Equal(2, restored.Messages.Count);
        Assert.Equal("user", restored.Messages[0].Role);
        Assert.Equal("assistant", restored.Messages[1].Role);
    }

    [Fact]
    public void ConversationTranscript_LoadFromDisk_NonExistentFile_ReturnsNull()
    {
        var result = ConversationTranscript.LoadFromDisk("/nonexistent/path/file.json");
        Assert.Null(result);
    }

    [Fact]
    public void ConversationTranscript_SaveToDisk_LoadFromDisk_RoundTrips()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"transcript_test_{Guid.NewGuid()}.json");
        try
        {
            var transcript = new ConversationTranscript();
            transcript.AddUser("Hello");
            transcript.AddAssistant("World");
            transcript.SaveToDisk(tempFile);

            var loaded = ConversationTranscript.LoadFromDisk(tempFile);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Messages.Count);
            Assert.Equal("Hello", loaded.Messages[0].Content);
            Assert.Equal("World", loaded.Messages[1].Content);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ConversationTranscript_StartedAt_DefaultsToUtcNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var transcript = new ConversationTranscript();
        var after = DateTime.UtcNow.AddSeconds(1);
        Assert.True(transcript.StartedAt >= before);
        Assert.True(transcript.StartedAt <= after);
    }
}