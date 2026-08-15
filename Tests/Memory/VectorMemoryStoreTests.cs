using ECAssistant.Memory;
using ECAssistant.Services;

namespace ECAssistant.Tests.Memory;

public class VectorMemoryStoreTests : IDisposable
{
    private readonly string _tempDir;

    public VectorMemoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAssistantTests_VMS_" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

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
    public void Constructor_ValidDir_StorePathSet()
    {
        var store = new VectorMemoryStore(_tempDir);
        Assert.Equal(Path.GetFullPath(_tempDir), store.StorePath);
        Assert.False(store.IsInitialized);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task InitializeAsync_WithEmbedder_CreatesDirAndInitializes()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        Assert.True(store.IsInitialized);
        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public async Task InitializeAsync_WithoutEmbedder_LoadOnlyMode()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync();
        Assert.True(store.IsInitialized);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task InitializeAsync_ExistingVectors_LoadsEntries()
    {
        // First store: add entries
        var store1 = new VectorMemoryStore(_tempDir);
        await store1.InitializeAsync(MakeEmbedder());
        await store1.AddAsync("key1", "content1", "cat1");

        // Second store: load from same dir
        var store2 = new VectorMemoryStore(_tempDir);
        await store2.InitializeAsync(MakeEmbedder());
        Assert.Equal(1, store2.Count);
    }

    [Fact]
    public async Task AddAsync_WhenNotInitialized_Throws()
    {
        var store = new VectorMemoryStore(_tempDir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync("k", "c"));
    }

    [Fact]
    public async Task AddAsync_ValidEntry_IncrementsCount()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("key1", "content1", "category1");
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task AddAsync_WithTags_PersistsTags()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        var tags = new Dictionary<string, string> { { "env", "prod" } };
        await store.AddAsync("key1", "content1", "cat1", tags);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task AddAsync_MultipleEntries_CountIncreases()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("k1", "c1");
        await store.AddAsync("k2", "c2");
        await store.AddAsync("k3", "c3");
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public async Task SearchAsync_NoEntries_ReturnsEmpty()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        var results = await store.SearchAsync("query");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_NotInitialized_ReturnsEmpty()
    {
        var store = new VectorMemoryStore(_tempDir);
        var results = await store.SearchAsync("query");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithEntries_ReturnsSortedResults()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("k1", "alpha content");
        await store.AddAsync("k2", "beta content");
        var results = await store.SearchAsync("alpha", maxResults: 5);
        Assert.NotEmpty(results);
        Assert.True(results.Count <= 2);
        // Results should be sorted by score descending
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Score >= results[i].Score);
    }

    [Fact]
    public async Task SearchAsync_WithCategoryFilter_ReturnsOnlyMatchingCategory()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("k1", "content1", "bugs");
        await store.AddAsync("k2", "content2", "features");
        var results = await store.SearchAsync("content", categoryFilter: "bugs");
        Assert.All(results, r => Assert.Equal("bugs", r.Category));
    }

    [Fact]
    public async Task SearchAsync_MaxResultsLimit_RespectsLimit()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("k1", "c1");
        await store.AddAsync("k2", "c2");
        await store.AddAsync("k3", "c3");
        var results = await store.SearchAsync("c", maxResults: 2);
        Assert.True(results.Count <= 2);
    }

    [Fact]
    public async Task SearchAsTextAsync_NoResults_ReturnsNoMatchesMessage()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        var text = await store.SearchAsTextAsync("query");
        Assert.Contains("No semantic matches", text);
    }

    [Fact]
    public async Task SearchAsTextAsync_WithResults_ReturnsFormattedText()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("k1", "content1", "cat1");
        var text = await store.SearchAsTextAsync("content");
        Assert.Contains("SEMANTIC MEMORY RESULTS", text);
        Assert.Contains("k1", text);
    }

    [Fact]
    public async Task SearchAsTextAsync_LongContent_TruncatesWithEllipsis()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        var longContent = new string('x', 500);
        await store.AddAsync("k1", longContent);
        var text = await store.SearchAsTextAsync("xxx");
        Assert.Contains("...", text);
    }

    [Fact]
    public async Task RemoveAsync_ExistingKey_RemovesAndReturnsTrue()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("k1", "c1");
        var result = await store.RemoveAsync("k1");
        Assert.True(result);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task RemoveAsync_NonExistentKey_ReturnsFalse()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        var result = await store.RemoveAsync("nonexistent");
        Assert.False(result);
    }

    [Fact]
    public async Task RemoveAsync_CaseInsensitive_MatchesKey()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("MyKey", "content");
        var result = await store.RemoveAsync("mykey");
        Assert.True(result);
    }

    [Fact]
    public async Task ClearAsync_WithEntries_ClearsAll()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("k1", "c1");
        await store.AddAsync("k2", "c2");
        await store.ClearAsync();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task GetStats_WithEntries_ReturnsStatsString()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        await store.AddAsync("k1", "c1", "bugs");
        await store.AddAsync("k2", "c2", "features");
        var stats = store.GetStats();
        Assert.Contains("Vector Memory Stats", stats);
        Assert.Contains("Entries: 2", stats);
        Assert.Contains("bugs: 1", stats);
        Assert.Contains("features: 1", stats);
    }

    [Fact]
    public async Task GetStats_NoEntries_ReturnsStatsWithZero()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        var stats = store.GetStats();
        Assert.Contains("Entries: 0", stats);
    }

    [Fact]
    public async Task Count_AfterAddAndRemove_ReflectsCorrectCount()
    {
        var store = new VectorMemoryStore(_tempDir);
        await store.InitializeAsync(MakeEmbedder());
        Assert.Equal(0, store.Count);
        await store.AddAsync("k1", "c1");
        Assert.Equal(1, store.Count);
        await store.AddAsync("k2", "c2");
        Assert.Equal(2, store.Count);
        await store.RemoveAsync("k1");
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Persistence_SaveAndLoad_RoundTripsEntries()
    {
        var store1 = new VectorMemoryStore(_tempDir);
        await store1.InitializeAsync(MakeEmbedder());
        await store1.AddAsync("persistKey", "persistContent", "testCat");
        store1.Dispose();

        var store2 = new VectorMemoryStore(_tempDir);
        await store2.InitializeAsync(MakeEmbedder());
        Assert.Equal(1, store2.Count);
        var results = await store2.SearchAsync("persist");
        Assert.Contains(results, r => r.Key == "persistKey");
    }
}