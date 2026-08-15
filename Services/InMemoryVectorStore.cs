using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// In-memory vector store implementation.
/// </summary>
public class InMemoryVectorStore : IVectorStore
{
    private readonly List<VectorEntry> _entries = new();

    public async Task IndexAsync(string content, string metadata)
    {
        await Task.Run(() =>
        {
            _entries.Add(new VectorEntry(content, metadata));
        });
    }

    public async Task<List<VectorResult>> SearchAsync(float[] embedding, int maxResults = 5)
    {
        return await Task.Run(() =>
        {
            var results = new List<VectorResult>();

            foreach (var entry in _entries)
            {
                var score = entry.Embedding != null ? CosineSimilarity(embedding, entry.Embedding) : 0f;
                results.Add(new VectorResult(entry.Content, entry.Metadata, score));
            }

            results.Sort((a, b) => b.Score.CompareTo(a.Score));
            return results.Take(maxResults).ToList();
        });
    }

    private float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        var magA = 0f;
        var magB = 0f;

        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0f;
        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    private record VectorEntry(string Content, string Metadata, float[]? Embedding = null);
}