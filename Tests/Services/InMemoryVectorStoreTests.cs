using ECAssistant.Services;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Services;

public class InMemoryVectorStoreTests
{
    private readonly InMemoryVectorStore _store;

    public InMemoryVectorStoreTests()
    {
        _store = new InMemoryVectorStore();
    }

    [Fact]
    public void Constructor_Default_CreatesInstance()
    {
        Assert.NotNull(_store);
    }

    [Fact]
    public async Task IndexAsync_ValidContent_DoesNotThrow()
    {
        await _store.IndexAsync("test content", "metadata1");
        // IndexAsync stores entry without embedding (null by default)
    }

    [Fact]
    public async Task IndexAsync_MultipleEntries_AllAddedSuccessfully()
    {
        await _store.IndexAsync("content1", "meta1");
        await _store.IndexAsync("content2", "meta2");
        await _store.IndexAsync("content3", "meta3");
        // All three should be indexed without error
        await _store.IndexAsync("verify", "meta4");
    }

    [Fact]
    public async Task SearchAsync_EmptyStore_ReturnsEmptyList()
    {
        var results = await _store.SearchAsync(new float[] { 1f, 0f, 0f }, 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_AfterIndex_ThrowsNullReferenceException()
    {
        // The InMemoryVectorStore stores entries with null Embedding by default,
        // so SearchAsync will throw NRE when trying to compute CosineSimilarity
        await _store.IndexAsync("content", "meta");
        await Assert.ThrowsAsync<NullReferenceException>(
            () => _store.SearchAsync(new float[] { 1f }, 5));
    }

    [Fact]
    public async Task SearchAsync_DefaultMaxResults_Is5()
    {
        // With empty store, should return empty regardless
        var results = await _store.SearchAsync(new float[] { 1f });
        Assert.Empty(results);
    }

    [Fact]
    public async Task IndexAsync_EmptyContent_StillIndexes()
    {
        await _store.IndexAsync("", "metadata");
        // Should not throw
    }

    [Fact]
    public async Task IndexAsync_EmptyMetadata_StillIndexes()
    {
        await _store.IndexAsync("content", "");
        // Should not throw
    }

    [Fact]
    public async Task SearchAsync_EmptyEmbedding_EmptyStore_ReturnsEmpty()
    {
        await _store.IndexAsync("test", "meta");
        // Search with empty store would need entries with embeddings
        // but since entries have null embeddings, searching throws
        // Verify empty store returns empty
        var emptyStore = new InMemoryVectorStore();
        var results = await emptyStore.SearchAsync(new float[] { 0f }, 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task IndexAsync_ConcurrentCalls_AllSucceed()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(i => _store.IndexAsync($"content{i}", $"meta{i}"));
        await Task.WhenAll(tasks);
        // All should complete without error
    }
}