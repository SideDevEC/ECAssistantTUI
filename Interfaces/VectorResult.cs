namespace ECAssistant.Interfaces;

public record VectorResult(
    string Content,
    string Metadata,
    float Score
);