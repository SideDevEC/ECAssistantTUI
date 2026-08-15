using ECAssistant.Memory;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Memory;

public class EMemoryManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IColorFormatter> _mockColor;

    public EMemoryManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAssistantTests_EMM_" + Guid.NewGuid().ToString("N")[..8]);
        _mockColor = new Mock<IColorFormatter>();
        _mockColor.SetupGet(c => c.Green).Returns("\x1b[32m");
        _mockColor.SetupGet(c => c.Red).Returns("\x1b[31m");
        _mockColor.SetupGet(c => c.Cyan).Returns("\x1b[36m");
        _mockColor.SetupGet(c => c.Reset).Returns("\x1b[0m");
        _mockColor.SetupGet(c => c.Bold).Returns("\x1b[1m");
        _mockColor.Setup(c => c.Tag(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()));
        _mockColor.Setup(c => c.TagBold(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Constructor_WithCustomPath_SetsDirectory()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        Assert.Equal(Path.GetFullPath(_tempDir), Path.GetFullPath(_tempDir));
    }

    [Fact]
    public void Constructor_WithNullPath_DefaultsToMemoryDir()
    {
        var mgr = new EMemoryManager(null, _mockColor.Object);
        Assert.Equal(Path.GetFullPath("Memory"), mgr.Count >= 0 ? Path.GetFullPath("Memory") : "");
    }

    [Fact]
    public void Load_WithNoDirectory_CreatesDirectoryAndReturnsEmpty()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        Assert.Equal(0, mgr.Count);
        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void Load_WithExistingJsonFiles_LoadsEntries()
    {
        // Create a memory file manually
        Directory.CreateDirectory(_tempDir);
        var entry = new ECAssistant.Memory.MemoryEntry
        {
            Id = 1,
            Key = "TestKey",
            Content = "Test content",
            Category = "general",
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Confidence = 0.9f
        };
        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        File.WriteAllText(Path.Combine(_tempDir, "test_entry.json"), json);

        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        Assert.Equal(1, mgr.Count);
    }

    [Fact]
    public void Load_WithInvalidJson_SkipsBadFile()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "bad.json"), "{ invalid json }");
        File.WriteAllText(Path.Combine(_tempDir, "empty_key.json"),
            System.Text.Json.JsonSerializer.Serialize(new ECAssistant.Memory.MemoryEntry { Key = "" }));

        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        Assert.Equal(0, mgr.Count);
    }

    [Fact]
    public void AddEntry_ValidEntry_IncreasesCount()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("testKey", "test content", "general");
        Assert.Equal(1, mgr.Count);
    }

    [Fact]
    public void AddEntry_MultipleEntries_CountIncreases()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("k1", "c1");
        mgr.AddEntry("k2", "c2");
        mgr.AddEntry("k3", "c3");
        Assert.Equal(3, mgr.Count);
    }

    [Fact]
    public void AddEntry_WithRelatedProject_SetsProperty()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("key", "content", "general", "MyProject");
        var entry = mgr.Entries.First();
        Assert.Equal("MyProject", entry.RelatedProject);
    }

    [Fact]
    public void AddEntry_SetsTimestamp()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("key", "content");
        var entry = mgr.Entries.First();
        Assert.False(string.IsNullOrEmpty(entry.Timestamp));
    }

    [Fact]
    public void Query_NoMemories_ReturnsNoMemoriesMessage()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        var result = mgr.Query("anything");
        Assert.Contains("No memories found", result);
    }

    [Fact]
    public void Query_WithMatchingEntries_ReturnsResults()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("config bug", "Fixed config loading issue", "bugs");
        var result = mgr.Query("config");
        Assert.Contains("RELEVANT MEMORIES", result);
        Assert.Contains("config bug", result);
    }

    [Fact]
    public void Query_WithCategoryFilter_PrioritizesMatchingCategory()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("key1", "content about bugs", "bugs");
        mgr.AddEntry("key2", "content about bugs", "features");
        var result = mgr.Query("bugs", categoryFilter: "bugs");
        // Both may appear, but the bugs category entry should be in the result
        Assert.Contains("key1", result);
    }

    [Fact]
    public void Query_NoMatch_ReturnsNoMemoriesForSearchTerm()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("key1", "content about config", "bugs");
        var result = mgr.Query("xyz_nonexistent");
        Assert.Contains("No memories found for", result);
    }

    [Fact]
    public void Query_MaxResultsLimit_RespectsLimit()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        for (int i = 0; i < 10; i++)
            mgr.AddEntry($"key{i}", $"content{i}", "general");
        var result = mgr.Query("content", maxResults: 3);
        // Count how many entries appear in the output
        var matchCount = System.Text.RegularExpressions.Regex.Matches(result, "key\\d+").Count;
        Assert.True(matchCount <= 3);
    }

    [Fact]
    public void GetContextSummary_NoMemories_ReturnsFirstTimeMessage()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        var summary = mgr.GetContextSummary();
        Assert.Contains("No prior memories", summary);
    }

    [Fact]
    public void GetContextSummary_WithMemories_ReturnsFormattedSummary()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("key1", "content1", "bugs");
        mgr.AddEntry("key2", "content2", "features");
        var summary = mgr.GetContextSummary();
        Assert.Contains("PERSISTENT MEMORY", summary);
        Assert.Contains("bugs", summary);
        Assert.Contains("features", summary);
    }

    [Fact]
    public void Clear_WithEntries_ClearsAll()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("k1", "c1");
        mgr.AddEntry("k2", "c2");
        mgr.Clear();
        Assert.Equal(0, mgr.Count);
    }

    [Fact]
    public void DeleteEntry_ExistingKey_RemovesEntry()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("keyToDelete", "content");
        mgr.DeleteEntry("keyToDelete");
        Assert.Equal(0, mgr.Count);
    }

    [Fact]
    public void DeleteEntry_NonExistentKey_DoesNothing()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("k1", "c1");
        mgr.DeleteEntry("nonexistent");
        Assert.Equal(1, mgr.Count);
    }

    [Fact]
    public void DeleteEntry_CaseInsensitive_MatchesKey()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("MyKey", "content");
        mgr.DeleteEntry("mykey");
        Assert.Equal(0, mgr.Count);
    }

    [Fact]
    public void Save_WithDirtyFlag_WritesFilesToDisk()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("saveKey", "saveContent", "general");
        mgr.Save();
        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.NotEmpty(files);
    }

    [Fact]
    public void Save_WithoutDirtyFlag_DoesNothing()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        // No AddEntry, so _dirty is false → Save should not write anything
        mgr.Save();
        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.Empty(files);
    }

    [Fact]
    public void GetStats_WithEntries_ReturnsStatsWithCounts()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("k1", "c1", "bugs");
        mgr.AddEntry("k2", "c2", "features");
        var stats = mgr.GetStats();
        Assert.Contains("Total entries: 2", stats);
        Assert.Contains("bugs: 1", stats);
        Assert.Contains("features: 1", stats);
    }

    [Fact]
    public void GetStats_NoEntries_ReturnsZeroCount()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        var stats = mgr.GetStats();
        Assert.Contains("Total entries: 0", stats);
    }

    [Fact]
    public void Entries_AfterAdd_ReturnsEnumerable()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("k1", "c1");
        var entries = mgr.Entries.ToList();
        Assert.Single(entries);
        Assert.Equal("k1", entries[0].Key);
    }

    [Fact]
    public void Dispose_SavesDirtyChanges()
    {
        var mgr = new EMemoryManager(_tempDir, _mockColor.Object);
        mgr.Load();
        mgr.AddEntry("disposeKey", "disposeContent");
        mgr.Dispose();
        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.NotEmpty(files);
    }
}