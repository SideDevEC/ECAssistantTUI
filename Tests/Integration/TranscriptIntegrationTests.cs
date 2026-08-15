using ECAssistant.Engine;

namespace ECAssistant.Tests.Integration;

/// <summary>
/// Integration tests for ConversationTranscript persistence —
/// exercises full save/load round-trips with real file system I/O.
/// </summary>
public class TranscriptIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public TranscriptIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAInteg_Transcript_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Transcript_AddMessages_SaveToDisk_LoadFromDisk_RoundTrip()
    {
        var transcript = new ConversationTranscript();
        transcript.AddSystem("You are a helpful assistant.");
        transcript.AddUser("What is the capital of France?");
        transcript.AddAssistant("The capital of France is Paris.");
        transcript.AddToolOutput("Verified: Paris", "EWebSearchTool");

        var filePath = Path.Combine(_tempDir, "transcript.json");
        transcript.SaveToDisk(filePath);

        var loaded = ConversationTranscript.LoadFromDisk(filePath);
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.MessageCount);

        // Verify roles and content
        Assert.Equal("system", loaded.Messages[0].Role);
        Assert.Equal("You are a helpful assistant.", loaded.Messages[0].Content);

        Assert.Equal("user", loaded.Messages[1].Role);
        Assert.Equal("What is the capital of France?", loaded.Messages[1].Content);

        Assert.Equal("assistant", loaded.Messages[2].Role);
        Assert.Equal("The capital of France is Paris.", loaded.Messages[2].Content);

        Assert.Equal("tool_output", loaded.Messages[3].Role);
        Assert.Equal("EWebSearchTool", loaded.Messages[3].Source);
        Assert.Equal("Verified: Paris", loaded.Messages[3].Content);
    }

    [Fact]
    public void TranscriptMessage_ToJson_FromJson_DataPreserved()
    {
        // Test all message types
        var testCases = new[]
        {
            (Msg: TranscriptMessage.System("system prompt"), Role: "system", Content: "system prompt"),
            (Msg: TranscriptMessage.User("user message", "user"), Role: "user", Content: "user message"),
            (Msg: TranscriptMessage.Assistant("assistant reply", "model-x"), Role: "assistant", Content: "assistant reply"),
            (Msg: TranscriptMessage.ToolOutput("tool result", "EShellAgent"), Role: "tool_output", Content: "tool result"),
        };

        foreach (var (msg, role, content) in testCases)
        {
            var json = msg.ToJson();
            var restored = TranscriptMessage.FromJson(json);

            Assert.Equal(role, restored.Role);
            Assert.Equal(content, restored.Content);
            Assert.Equal(msg.Source, restored.Source);
            Assert.Equal(msg.EstimatedTokens, restored.EstimatedTokens);
        }
    }

    [Fact]
    public void TranscriptMessage_WithEstimatedTokens_ToJson_FromJson_PreservesTokens()
    {
        var msg = TranscriptMessage.User("Hello world");
        msg.EstimatedTokens = 42;

        var json = msg.ToJson();
        var restored = TranscriptMessage.FromJson(json);

        Assert.Equal(42, restored.EstimatedTokens);
        Assert.Equal("Hello world", restored.Content);
    }

    [Fact]
    public void Transcript_LargeTranscript_100Messages_SaveToDisk_LoadFromDisk_AllLoaded()
    {
        var transcript = new ConversationTranscript();

        // Add a system message
        transcript.AddSystem("System prompt for large transcript test.");

        // Add 99 alternating user/assistant messages
        for (int i = 0; i < 49; i++)
        {
            transcript.AddUser($"User message number {i}. " +
                $"This is some content to make the message realistic with tokens. " +
                $"Iteration {i} of the large transcript test.");
            transcript.AddAssistant($"Assistant response number {i}. " +
                $"Here is my response to your question. " +
                $"This is iteration {i} of the test conversation.");
        }
        // Add one more user message to reach 100 total
        transcript.AddUser("Final user message.");

        Assert.Equal(100, transcript.MessageCount);

        var filePath = Path.Combine(_tempDir, "large_transcript.json");
        transcript.SaveToDisk(filePath);
        Assert.True(File.Exists(filePath));

        var loaded = ConversationTranscript.LoadFromDisk(filePath);
        Assert.NotNull(loaded);
        Assert.Equal(100, loaded!.MessageCount);

        // Verify first and last messages
        Assert.Equal("system", loaded.Messages[0].Role);
        Assert.Equal("user", loaded.Messages[99].Role);
        Assert.Equal("Final user message.", loaded.Messages[99].Content);

        // Spot check a few in the middle
        Assert.Equal("user", loaded.Messages[1].Role);
        Assert.Contains("User message number 0", loaded.Messages[1].Content);
        Assert.Equal("assistant", loaded.Messages[2].Role);
        Assert.Contains("Assistant response number 0", loaded.Messages[2].Content);

        Assert.Equal("assistant", loaded.Messages[98].Role);
        Assert.Contains("Assistant response number 48", loaded.Messages[98].Content);
    }

    [Fact]
    public void Transcript_LoadFromDisk_NonExistentFile_ReturnsNull()
    {
        var result = ConversationTranscript.LoadFromDisk(Path.Combine(_tempDir, "no_such_file.json"));
        Assert.Null(result);
    }

    [Fact]
    public void Transcript_SaveToDisk_CreatesValidJsonFile()
    {
        var transcript = new ConversationTranscript();
        transcript.AddUser("Test message");
        transcript.AddAssistant("Test response");

        var filePath = Path.Combine(_tempDir, "verify_json.json");
        transcript.SaveToDisk(filePath);

        var content = File.ReadAllText(filePath);
        Assert.Contains("\"Messages\"", content);
        Assert.Contains("Test message", content);
        Assert.Contains("Test response", content);

        // Verify it's valid JSON by parsing
        using var doc = System.Text.Json.JsonDocument.Parse(content);
        Assert.True(doc.RootElement.TryGetProperty("Messages", out _));
    }

    [Fact]
    public void Transcript_StartedAt_PreservedAcrossSaveLoad()
    {
        var transcript = new ConversationTranscript();
        var originalStartedAt = transcript.StartedAt;
        transcript.AddUser("Test");

        var filePath = Path.Combine(_tempDir, "timestamp.json");
        transcript.SaveToDisk(filePath);

        var loaded = ConversationTranscript.LoadFromDisk(filePath);
        Assert.NotNull(loaded);
        Assert.Equal(originalStartedAt, loaded!.StartedAt);
    }

    [Fact]
    public void Transcript_Empty_SaveToDisk_LoadFromDisk_RoundTrip()
    {
        var transcript = new ConversationTranscript();
        var filePath = Path.Combine(_tempDir, "empty_transcript.json");
        transcript.SaveToDisk(filePath);

        var loaded = ConversationTranscript.LoadFromDisk(filePath);
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.MessageCount);
        Assert.Empty(loaded.Messages);
    }

    [Fact]
    public void Transcript_AddRange_SaveLoad_AllMessagesRoundTrip()
    {
        var messages = new List<TranscriptMessage>
        {
            TranscriptMessage.User("msg1"),
            TranscriptMessage.Assistant("msg2"),
            TranscriptMessage.ToolOutput("msg3", "tool1"),
            TranscriptMessage.User("msg4"),
            TranscriptMessage.Assistant("msg5"),
        };

        var transcript = new ConversationTranscript();
        transcript.AddRange(messages);
        Assert.Equal(5, transcript.MessageCount);

        var filePath = Path.Combine(_tempDir, "range_transcript.json");
        transcript.SaveToDisk(filePath);

        var loaded = ConversationTranscript.LoadFromDisk(filePath);
        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.MessageCount);
        Assert.Equal("msg1", loaded.Messages[0].Content);
        Assert.Equal("msg5", loaded.Messages[4].Content);
    }
}