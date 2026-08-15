namespace ECAssistant.Interfaces;

public record GenerationParams(
    int MaxTokens,
    float Temperature,
    float TopP,
    int TopK,
    float RepeatPenalty
);