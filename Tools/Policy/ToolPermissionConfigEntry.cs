using System.Text.Json.Serialization;

namespace ECAssistant.Tools;

/// <summary>
/// Config entry for tool permissions in appsettings.json.
/// </summary>
public class ToolPermissionConfigEntry
{
    [JsonPropertyName("tool")]
    public string ToolName { get; set; } = "";

    [JsonPropertyName("level")]
    public string Level { get; set; } = "Allowed";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}