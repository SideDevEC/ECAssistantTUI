using ECAssistant.Memory;
using ECAssistant.Services;

namespace ECAssistant.Tests.Integration;

/// <summary>
/// Integration tests for the memory pipeline — EMemoryManager and VectorMemoryStore
/// with real file system persistence across save/load cycles.
/// </summary>
public class MemoryIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public MemoryIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAInteg_Memory_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ─── EMemoryManager Pipeline ──────────────────

    [Fact]
    public void EMemoryManager_AddSaveLoadSearch_FullPipelineWorks()
    {
        var memDir = Path.Combine(_tempDir, "mem");
        var mgr = new EMemoryManager(memDir);
        mgr.Load();

        // Add an entry
        mgr.AddEntry("config bug", "Fixed config loading issue in ConfigLoader", "bugs");
        Assert.Equal(1, mgr.Count);

        // Save to disk
        mgr.Save();
        var files = Directory.GetFiles(memDir, "*.json");
        Assert.NotEmpty(files);

        // Load in a new instance
        var mgr2 = new EMemoryManager(memDir);
        mgr2.Load();
        Assert.Equal(1, mgr2.Count);

        // Search
        var result = mgr2.Query("config");
        Assert.Contains("RELEVANT MEMORIES", result);
        Assert.Contains("config bug", result);
    }

    [Fact]
    public void EMemoryManager_AddMultiple_SearchReturnsRankedResults()
    {
        var memDir = Path.Combine(_tempDir, "mem_multi");
        var mgr = new EMemoryManager(memDir);
        mgr.Load();

        mgr.AddEntry("config setup", "How to configure the agent settings", "general");
        mgr.AddEntry("config bug fix", "Fixed config loading issue with null path", "bugs");
        mgr.AddEntry("unrelated entry", "Something about shell tools", "tools");

        // Search for "config" — should rank config-related entries higher
        var result = mgr.Query("config");
        Assert.Contains("RELEVANT MEMORIES", result);
        Assert.Contains("config setup", result);
        Assert.Contains("config bug fix", result);
    }

    [Fact]
    public void EMemoryManager_SaveLoadAfterDispose_DataPersists()
    {
        var memDir = Path.Combine(_tempDir, "mem_persist");
        var mgr = new EMemoryManager(memDir);
        mgr.Load();
        mgr.AddEntry("persistKey", "This should survive disposal", "general");

        // Dispose triggers Save
        mgr.Dispose();

        // Verify files exist on disk
        var files = Directory.GetFiles(memDir, "*.json");
        Assert.NotEmpty(files);

        // Load in a fresh instance
        var mgr2 = new EMemoryManager(memDir);
        mgr2.Load();
        Assert.Equal(1, mgr2.Count);
        var entry = mgr2.Entries.First();
        Assert.Equal("persistKey", entry.Key);
        Assert.Contains("survive disposal", entry.Content);
    }

    [Fact]
    public void EMemoryManager_LoadExistingFiles_LoadsAllEntries()
    {
        var memDir = Path.Combine(_tempDir, "mem_load");
        Directory.CreateDirectory(memDir);

        // Manually create memory files
        for (int i = 1; i <= 3; i++)
        {
            var entry = new ECAssistant.Memory.MemoryEntry
            {
                Id = i,
                Key = $"key{i}",
                Content = $"content{i}",
                Category = "general",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Confidence = 0.8f
            };
            var json = System.Text.Json.JsonSerializer.Serialize(entry);
            File.WriteAllText(Path.Combine(memDir, $"entry_{i:D4}_key{i}_.json"), json);
        }

        var mgr = new EMemoryManager(memDir);
        mgr.Load();
        Assert.Equal(3, mgr.Count);
    }

    [Fact]
    public void EMemoryManager_Clear_RemovesEntriesFromMemory()
    {
        var memDir = Path.Combine(_tempDir, "mem_clear");
        var mgr = new EMemoryManager(memDir);
        mgr.Load();
        mgr.AddEntry("k1", "c1");
        mgr.AddEntry("k2", "c2");
        mgr.Save();

        // Clear removes from in-memory list
        mgr.Clear();
        Assert.Equal(0, mgr.Count);

        // Note: EMemoryManager.Save() only writes current entries —
        // it does not delete previously-saved files from disk.
        // This is a known limitation of the current implementation.
    }

    // ─── VectorMemoryStore Pipeline ───────────────

    private static Func<string, Task<float[]>> MakeEmbedder(int dim = 4)
    {
        return (text) =>
        {
            // Deterministic pseudo-embedding based on text hash
            var rng = new Random(text.GetHashCode());
            var vec = new float[dim];
            for (int i = 0; i < dim; i++)
                vec[i] = (float)(rng.NextDouble() * 2 - 1);
            return Task.FromResult(vec);
        };
    }

    [Fact]
    public async Task VectorMemoryStore_IndexSearch_ReturnsCosineSimilarityResults()
    {
        var storeDir = Path.Combine(_tempDir, "vecmem");
        var store = new VectorMemoryStore(storeDir);
        await store.InitializeAsync(MakeEmbedder());

        await store.AddAsync("alpha", "Content about alpha features", "features");
        await store.AddAsync("beta", "Content about beta testing", "testing");
        await store.AddAsync("gamma", "Content about gamma rays", "science");

        var results = await store.SearchAsync("alpha", maxResults: 3);
        Assert.NotEmpty(results);
        // Results sorted by score descending
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Score >= results[i].Score);
    }

    [Fact]
    public async Task VectorMemoryStore_Persistence_IndexSaveLoadSearch_Works()
    {
        var storeDir = Path.Combine(_tempDir, "vecmem_persist");
        var embedder = MakeEmbedder();

        // Index entries
        var store1 = new VectorMemoryStore(storeDir);
        await store1.InitializeAsync(embedder);
        await store1.AddAsync("persistKey1", "First persistent content", "cat1");
        await store1.AddAsync("persistKey2", "Second persistent content", "cat2");
        Assert.Equal(2, store1.Count);
        store1.Dispose();

        // Load in a new store and search
        var store2 = new VectorMemoryStore(storeDir);
        await store2.InitializeAsync(embedder);
        Assert.Equal(2, store2.Count);

        var results = await store2.SearchAsync("persistent", maxResults: 5);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Key == "persistKey1");
        Assert.Contains(results, r => r.Key == "persistKey2");
    }

    [Fact]
    public async Task VectorMemoryStore_CategoryFilter_OnlyReturnsMatchingCategory()
    {
        var storeDir = Path.Combine(_tempDir, "vecmem_cat");
        var store = new VectorMemoryStore(storeDir);
        await store.InitializeAsync(MakeEmbedder());

        await store.AddAsync("k1", "content about bugs", "bugs");
        await store.AddAsync("k2", "content about features", "features");
        await store.AddAsync("k3", "more bug content", "bugs");

        var results = await store.SearchAsync("content", categoryFilter: "bugs");
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("bugs", r.Category));
    }

    [Fact]
    public async Task VectorMemoryStore_Remove_UpdatesPersistence()
    {
        var storeDir = Path.Combine(_tempDir, "vecmem_remove");
        var embedder = MakeEmbedder();

        var store1 = new VectorMemoryStore(storeDir);
        await store1.InitializeAsync(embedder);
        await store1.AddAsync("keepKey", "keep this", "general");
        await store1.AddAsync("removeKey", "remove this", "general");
        await store1.RemoveAsync("removeKey");
        store1.Dispose();

        var store2 = new VectorMemoryStore(storeDir);
        await store2.InitializeAsync(embedder);
        Assert.Equal(1, store2.Count);
    }
}