using ECAssistant.Services;
using ECAssistant.Interfaces;
using Moq;

namespace ECAssistant.Tests.Services;

public class MemoryServiceTests
{
    private readonly Mock<IFileSystem> _mockFileSystem;
    private readonly Mock<IVectorStore> _mockVectorStore;
    private readonly Mock<IConfigProvider> _mockConfigProvider;
    private readonly Mock<IVectorEmbedder> _mockEmbedder;

    public MemoryServiceTests()
    {
        _mockFileSystem = new Mock<IFileSystem>();
        _mockVectorStore = new Mock<IVectorStore>();
        _mockConfigProvider = new Mock<IConfigProvider>();
        _mockEmbedder = new Mock<IVectorEmbedder>();
    }

    [Fact]
    public void Constructor_ValidArguments_CreatesInstance()
    {
        var service = new MemoryService(
            _mockFileSystem.Object,
            _mockVectorStore.Object,
            _mockConfigProvider.Object,
            _mockEmbedder.Object);
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_NullFileSystem_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryService(
            null!, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object));
    }

    [Fact]
    public void Constructor_NullVectorStore_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryService(
            _mockFileSystem.Object, null!, _mockConfigProvider.Object, _mockEmbedder.Object));
    }

    [Fact]
    public void Constructor_NullConfigProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, null!, _mockEmbedder.Object));
    }

    [Fact]
    public void Constructor_NullEmbedder_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, null!));
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_ReturnsResults()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        _mockEmbedder.Setup(e => e.Embed("test query")).Returns(embedding);
        var vectorResults = new List<VectorResult>
        {
            new("content1", "meta1", 0.9f),
            new("content2", "meta2", 0.5f)
        };
        _mockVectorStore.Setup(vs => vs.SearchAsync(embedding, 5))
            .ReturnsAsync(vectorResults);

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        var results = await service.SearchAsync("test query");

        Assert.Equal(2, results.Count);
        Assert.Equal("content1", results[0].Content);
        Assert.Equal("meta1", results[0].Metadata);
    }

    [Fact]
    public async Task SearchAsync_CallsEmbedderWithQuery()
    {
        _mockEmbedder.Setup(e => e.Embed(It.IsAny<string>())).Returns(new float[] { 0f });
        _mockVectorStore.Setup(vs => vs.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>()))
            .ReturnsAsync(new List<VectorResult>());

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await service.SearchAsync("my query");

        _mockEmbedder.Verify(e => e.Embed("my query"), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_CallsVectorStoreWithEmbedding()
    {
        var embedding = new float[] { 1f, 0f };
        _mockEmbedder.Setup(e => e.Embed(It.IsAny<string>())).Returns(embedding);
        _mockVectorStore.Setup(vs => vs.SearchAsync(embedding, It.IsAny<int>()))
            .ReturnsAsync(new List<VectorResult>());

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await service.SearchAsync("test");

        _mockVectorStore.Verify(vs => vs.SearchAsync(embedding, It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_CustomMaxResults_PassesToVectorStore()
    {
        _mockEmbedder.Setup(e => e.Embed(It.IsAny<string>())).Returns(new float[] { 0f });
        _mockVectorStore.Setup(vs => vs.SearchAsync(It.IsAny<float[]>(), 10))
            .ReturnsAsync(new List<VectorResult>());

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await service.SearchAsync("test", 10);

        _mockVectorStore.Verify(vs => vs.SearchAsync(It.IsAny<float[]>(), 10), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_EmptyResults_ReturnsEmptyList()
    {
        _mockEmbedder.Setup(e => e.Embed(It.IsAny<string>())).Returns(new float[] { 0f });
        _mockVectorStore.Setup(vs => vs.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>()))
            .ReturnsAsync(new List<VectorResult>());

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        var results = await service.SearchAsync("no matches");
        Assert.Empty(results);
    }

    [Fact]
    public async Task AddAsync_ValidEntry_CallsVectorStoreIndex()
    {
        var entry = new MemoryEntry("content", "metadata");
        _mockVectorStore.Setup(vs => vs.IndexAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await service.AddAsync(entry);

        _mockVectorStore.Verify(vs => vs.IndexAsync("content", "metadata"), Times.Once);
    }

    [Fact]
    public async Task AddAsync_NullEntry_ThrowsNullReferenceException()
    {
        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await Assert.ThrowsAsync<NullReferenceException>(() => service.AddAsync(null!));
    }

    [Fact]
    public async Task SaveAsync_CallsConfigProviderForPath()
    {
        _mockConfigProvider.Setup(c => c.GetValue("memory.data_path", "Memory")).Returns("CustomPath");

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await service.SaveAsync();

        _mockConfigProvider.Verify(c => c.GetValue("memory.data_path", "Memory"), Times.Once);
    }

    [Fact]
    public async Task SaveAsync_DoesNotThrow()
    {
        _mockConfigProvider.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns("Memory");

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await service.SaveAsync();
    }

    [Fact]
    public async Task LoadAsync_CallsConfigProviderForPath()
    {
        _mockConfigProvider.Setup(c => c.GetValue("memory.data_path", "Memory")).Returns("CustomPath");

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await service.LoadAsync();

        _mockConfigProvider.Verify(c => c.GetValue("memory.data_path", "Memory"), Times.Once);
    }

    [Fact]
    public async Task LoadAsync_DoesNotThrow()
    {
        _mockConfigProvider.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns("Memory");

        var service = new MemoryService(
            _mockFileSystem.Object, _mockVectorStore.Object, _mockConfigProvider.Object, _mockEmbedder.Object);

        await service.LoadAsync();
    }
}