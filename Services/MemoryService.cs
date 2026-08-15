using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// Persistent memory service with vector search.
/// </summary>
public class MemoryService : IMemoryService
{
    private readonly IFileSystem _fileSystem;
    private readonly IVectorStore _vectorStore;
    private readonly IConfigProvider _configProvider;
    private readonly IVectorEmbedder _embedder;
    private readonly List<MemoryEntry> _entries;

    public MemoryService(
        IFileSystem fileSystem,
        IVectorStore vectorStore,
        IConfigProvider configProvider,
        IVectorEmbedder embedder)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _entries = new List<MemoryEntry>();
    }

    public async Task<List<MemoryEntry>> SearchAsync(string query, int maxResults = 5)
    {
        var embedding = _embedder.Embed(query);
        var results = await _vectorStore.SearchAsync(embedding, maxResults);

        var entries = new List<MemoryEntry>();
        foreach (var result in results)
        {
            entries.Add(new MemoryEntry(result.Content, result.Metadata));
        }

        return entries;
    }

    public async Task AddAsync(MemoryEntry entry)
    {
        _entries.Add(entry);
        await _vectorStore.IndexAsync(entry.Content, entry.Metadata);
    }

    public async Task SaveAsync()
    {
        await Task.Run(() =>
        {
            var memoryPath = _configProvider.GetValue("memory.data_path", "Memory");
            // TODO: Serialize and save to file
        });
    }

    public async Task LoadAsync()
    {
        await Task.Run(() =>
        {
            var memoryPath = _configProvider.GetValue("memory.data_path", "Memory");
            // TODO: Load from file
        });
    }
}