namespace ECAssistant.Engine;

public class FileSnapshot
{
    public string SnapshotId { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
}