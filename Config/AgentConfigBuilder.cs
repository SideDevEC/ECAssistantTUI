using System.IO;
using System.Text.Json;
using ECAssistant.Config;

namespace ECAssistant;

/// <summary>
/// Fluent config builder for library consumers.
///
/// On Build():
/// 1. Resolve working directory: given path + "eca-data" appended (default: "./eca-data/")
/// 2. If appsettings.json exists there → load it, return it (JSON is source of truth)
/// 3. If not → generate appsettings.json with code values + defaults, return it
///
/// Code values (WithModel, ContextSize, etc.) are ONLY used to seed the
/// initial JSON. After that, the JSON is authoritative. End users edit
/// the JSON to change settings — they never see the code.
///
/// Usage:
///   // First run: generates .eca-data/appsettings.json with these values
///   var config = AgentConfigBuilder.Create()
///       .WithModel("/path/to/model.gguf")
///       .ContextSize(16384)
///       .GpuLayers(15)
///       .Build();
///
///   // Subsequent runs: loads .eca-data/appsettings.json, ignores code values
///   var config = AgentConfigBuilder.Create()
///       .WithModel("/path/to/model.gguf")  // ignored — JSON exists
///       .Build();
///
/// End users edit .eca-data/appsettings.json to tweak settings.
/// </summary>
public class AgentConfigBuilder
{
    private string _modelPath = "";
    private uint _contextSize = 16384;
    private int _gpuLayers = 0;
    private int _threads = -1;
    private int _maxTokens = 2048;
    private float _temperature = 0.3f;
    private float _topP = 0.9f;
    private int _topK = 40;
    private float _repeatPenalty = 1.1f;
    private string _workingDir = ".";
    private bool _enableVectorMemory = false;
    private bool _enableSubAgents = false;
    private bool _enableSecondaryModel = false;
    private string? _secondaryModelPath = null;

    private AgentConfigBuilder() { }

    /// <summary>Start building a config.</summary>
    public static AgentConfigBuilder Create() => new();

    /// <summary>Path to the GGUF model file. Used only when generating initial JSON.</summary>
    public AgentConfigBuilder WithModel(string path) { _modelPath = path; return this; }

    /// <summary>LLM context window size in tokens. Default: 16384. Seeds initial JSON only.</summary>
    public AgentConfigBuilder ContextSize(uint size) { _contextSize = size; return this; }

    /// <summary>GPU layers to offload. 0 = CPU only. Default: 0. Seeds initial JSON only.</summary>
    public AgentConfigBuilder GpuLayers(int layers) { _gpuLayers = layers; return this; }

    /// <summary>CPU threads. -1 = auto. Default: -1. Seeds initial JSON only.</summary>
    public AgentConfigBuilder Threads(int threads) { _threads = threads; return this; }

    /// <summary>Max tokens per response. Default: 2048. Seeds initial JSON only.</summary>
    public AgentConfigBuilder MaxTokens(int tokens) { _maxTokens = tokens; return this; }

    /// <summary>Sampling temperature. Default: 0.3. Seeds initial JSON only.</summary>
    public AgentConfigBuilder Temperature(float temp) { _temperature = temp; return this; }

    /// <summary>Top-p sampling. Default: 0.9. Seeds initial JSON only.</summary>
    public AgentConfigBuilder TopP(float topP) { _topP = topP; return this; }

    /// <summary>Top-k sampling. Default: 40. Seeds initial JSON only.</summary>
    public AgentConfigBuilder TopK(int topK) { _topK = topK; return this; }

    /// <summary>Repeat penalty. Default: 1.1. Seeds initial JSON only.</summary>
    public AgentConfigBuilder RepeatPenalty(float penalty) { _repeatPenalty = penalty; return this; }

    /// <summary>
    /// Base directory for agent data. "eca-data" is always appended.
    /// Default: "." → resolves to "./eca-data/".
    /// Example: .WorkingDirectory("/var/lib/myapp") → "/var/lib/myapp/eca-data/"
    /// </summary>
    public AgentConfigBuilder WorkingDirectory(string dir) { _workingDir = dir; return this; }

    /// <summary>Enable vector memory (semantic search). Default: false. Seeds initial JSON only.</summary>
    public AgentConfigBuilder EnableVectorMemory(bool enabled = true) { _enableVectorMemory = enabled; return this; }

    /// <summary>Enable sub-agents (parallel child agents). Default: false. Seeds initial JSON only.</summary>
    public AgentConfigBuilder EnableSubAgents(bool enabled = true) { _enableSubAgents = enabled; return this; }

    /// <summary>Enable secondary model. Default: false. Seeds initial JSON only.</summary>
    public AgentConfigBuilder EnableSecondaryModel(bool enabled = true) { _enableSecondaryModel = enabled; return this; }

    /// <summary>Path to secondary model GGUF. Seeds initial JSON only.</summary>
    public AgentConfigBuilder WithSecondaryModel(string path) { _secondaryModelPath = path; return this; }

    /// <summary>
    /// Build the EAgentConfig.
    ///
    /// 1. Resolve working dir: given path + "/eca-data/"
    /// 2. If appsettings.json exists there → load and return it (JSON wins)
    /// 3. If not → generate it with code values + defaults, then return
    /// </summary>
    public EAgentConfig Build()
    {
        // 1. Resolve working directory — always append "eca-data"
        var workingDir = Path.Combine(_workingDir, "eca-data");
        Directory.CreateDirectory(workingDir);

        var jsonPath = Path.Combine(workingDir, "appsettings.json");
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        // 2. JSON exists → load it, that's the config (source of truth)
        if (File.Exists(jsonPath))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                var config = JsonSerializer.Deserialize<EAgentConfig>(json, jsonOptions);
                if (config != null)
                {
                    // Ensure working dir paths point to the resolved location
                    config.RootPath = workingDir;
                    if (config.AgentSettings == null) config.AgentSettings = new AgentConfig();
                    config.AgentSettings.WorkingDirectory = workingDir;
                    return config;
                }
            }
            catch
            {
                // Corrupt JSON → fall through to generate fresh
            }
        }

        // 3. No JSON (or corrupt) → build from code values + defaults
        var freshConfig = new EAgentConfig
        {
            RootPath = workingDir,
            AgentSettings = new AgentConfig { WorkingDirectory = workingDir },
            Llm = new LlmConfig
            {
                ModelPath = _modelPath,
                ContextSize = _contextSize,
                GpuLayers = _gpuLayers,
                Threads = _threads,
            },
            Inference = new InferenceConfig
            {
                MaxTokens = _maxTokens,
                AntiPrompts = new[] { "User:", "\n```\n", "Question:", "### User", "<user>" },
            },
            Sampling = new SamplingConfig
            {
                Temperature = _temperature,
                TopP = _topP,
                TopK = _topK,
                RepeatPenalty = _repeatPenalty,
            },
            VectorMemory = new VectorMemoryConfig
            {
                Enabled = _enableVectorMemory,
                Directory = "vecmem",
                MaxResults = 5,
                AutoIndex = true,
            },
            SubAgent = new SubAgentConfig
            {
                Enabled = _enableSubAgents,
            },
            SecondaryModel = new SecondaryModelConfig
            {
                Enabled = _enableSecondaryModel && !string.IsNullOrEmpty(_secondaryModelPath),
                ModelPath = _secondaryModelPath ?? "",
                ContextSize = 4096,
                GpuLayers = 0,
                Temperature = 0.1f,
                TopP = 0.8f,
                TopK = 40,
                RepeatPenalty = 1.1f,
                MaxTokens = 512,
                AntiPrompts = new[] { "User:", "\n```\n", "Question:", "Assistant:", "###", "<user>", "<tooloutput>", "### User" },
            },
        };

        // Generate the JSON file so it exists for next time
        var freshJson = JsonSerializer.Serialize(freshConfig, jsonOptions);
        File.WriteAllText(jsonPath, freshJson);

        return freshConfig;
    }

    /// <summary>
    /// v10.24: Write an updated EAgentConfig back to appsettings.json.
    /// Uses config.RootPath to locate the file. Called when new tools are registered
    /// and their config sections are added to the Tools dictionary.
    /// </summary>
    public static void Update(EAgentConfig config)
    {
        var jsonPath = Path.Combine(config.RootPath, "appsettings.json");
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        var json = JsonSerializer.Serialize(config, jsonOptions);
        File.WriteAllText(jsonPath, json);
    }
}