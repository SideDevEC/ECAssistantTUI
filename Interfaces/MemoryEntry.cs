namespace ECAssistant.Interfaces;

public record MemoryEntry(
    string Content,
    string Metadata,
    float[]? Embedding = null
);