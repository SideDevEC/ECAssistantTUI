using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECAssistant.Interfaces;

namespace ECAssistant.Config;

/// <summary>App settings — matches the nested structure in appsettings.json</summary>
public class EAgentConfig
{
    [JsonPropertyName("root_path")]
    public string RootPath { get; set; } = "ECAssistant";

    [JsonPropertyName("memory")]
    public MemoryConfig Memory { get; set; } = new();

    [JsonPropertyName("workspace")]
    public WorkspaceConfig Workspace { get; set; } = new();

    [JsonPropertyName("tools")]
    public ToolsConfig Tools { get; set; } = new();

    [JsonPropertyName("llm")]
    public LlmConfig Llm { get; set; } = new();

    [JsonPropertyName("secondary_model")]
    public SecondaryModelConfig SecondaryModel { get; set; } = new();

    [JsonPropertyName("subagent")]
    public SubAgentConfig SubAgent { get; set; } = new();

    [JsonPropertyName("vector_memory")]
    public VectorMemoryConfig VectorMemory { get; set; } = new();

    [JsonPropertyName("inference")]
    public InferenceConfig Inference { get; set; } = new();

    [JsonPropertyName("context_management")]
    public ContextManagementConfig ContextManagement { get; set; } = new();

    [JsonPropertyName("sampling")]
    public SamplingConfig Sampling { get; set; } = new();

    [JsonPropertyName("agent")]
    public AgentConfig AgentSettings { get; set; } = new();

    [JsonPropertyName("interface")]
    public InterfaceConfig Interface { get; set; } = new();

    public string GetRootPath() => Path.GetFullPath(RootPath);
    public string GetMemoryDirectory() => Path.GetFullPath(Path.Combine(RootPath, "Memory"));
    public string GetWorkspaceDirectory() => Path.GetFullPath(Path.Combine(RootPath, "Workspace"));

    public void Save(string filePath = "appsettings.json")
    {
        File.WriteAllText(filePath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}