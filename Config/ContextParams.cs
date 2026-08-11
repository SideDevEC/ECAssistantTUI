namespace ECAssistant.Config;

/// <summary>Bridge class for backward compatibility. In 0.27+, use LLama.ContextParams directly.</summary>
public class ContextParams 
{
    public uint? ContextSize { get; set; } = 512;
    public int? GpuLayerCount { get; set; }
    public bool UseMemorymap { get; set; } = true;
    public int? Threads { get; set; }
    public uint? BatchSize { get; set; }
    public uint? UBatchSize { get; set; }
}
