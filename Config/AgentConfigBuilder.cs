using System.IO;
using System.Text.Json;
using ECAssistant.Config;
using ECAssistant.Services;

namespace ECAssistant;

/// <summary>
/// Fluent config builder for library consumers.
///
/// On first run, generates appsettings.json in the working directory with
/// the configured defaults. On subsequent runs, loads the existing JSON
/// and applies any code-level overrides on top.
///
/// Usage (simplest — just create with defaults, JSON is auto-generated):
///   var config = AgentConfigBuilder.Create()
///       .WithModel("/path/to/model.gguf")
///       .Build();
///   // Creates .eca-data/appsettings.json on first run
///   // Loads it on subsequent runs
///
/// Usage (with overrides — code wins over JSON):
///   var config = AgentConfigBuilder.Create()
///       .WithModel("/path/to/model.gguf")
///       .ContextSize(32768)    // overrides JSON value
///       .GpuLayers(15)         // overrides JSON value
///       .Build();
///
/// End users can edit .eca-data/appsettings.json to tweak settings
/// (model path, temperature, context size, etc.) without touching code.
/// </summary>
public class AgentConfigBuilder
{
    private string _modelPath = "";
    private uint? _contextSize = null;
    private int? _gpuLayers = null;
    private int? _threads = null;
    private int? _maxTokens = null;
    private float? _temperature = null;
    private float? _topP = null;
    private int? _topK = null;
    private float? _repeatPenalty = null;
    private string? _workingDir = null;
    private bool? _enableVectorMemory = null;
    private bool? _enableSubAgents = null;
    private bool? _enableSecondaryModel = null;
    private string? _secondaryModelPath = null;
    private bool _autoSaveJson = true;

    private AgentConfigBuilder() { }

    /// <summary>Start building a config.</summary>
    public static AgentConfigBuilder Create() => new();

    /// <summary>Path to the GGUF model file.</summary>
    public AgentConfigBuilder WithModel(string path) { _modelPath = path; return this; }

    /// <summary>LLM context window size in tokens. Default: 16384.</summary>
    public AgentConfigBuilder ContextSize(uint size) { _contextSize = size; return this; }

    /// <summary>GPU layers to offload. 0 = CPU only. Default: 0.</summary>
    public AgentConfigBuilder GpuLayers(int layers) { _gpuLayers = layers; return this; }

    /// <summary>CPU threads. -1 = auto. Default: -1.</summary>
    public AgentConfigBuilder Threads(int threads) { _threads = threads; return this; }

    /// <summary>Max tokens per response. Default: 2048.</summary>
    public AgentConfigBuilder MaxTokens(int tokens) { _maxTokens = tokens; return this; }

    /// <summary>Sampling temperature. Default: 0.3.</summary>
    public AgentConfigBuilder Temperature(float temp) { _temperature = temp; return this; }

    /// <summary>Top-p sampling. Default: 0.9.</summary>
    public AgentConfigBuilder TopP(float topP) { _topP = topP; return this; }

    /// <summary>Top-k sampling. Default: 40.</summary>
    public AgentConfigBuilder TopK(int topK) { _topK = topK; return this; }

    /// <summary>Repeat penalty. Default: 1.1.</summary>
    public AgentConfigBuilder RepeatPenalty(float penalty) { _repeatPenalty = penalty; return this; }

    /// <summary>
    /// Working directory for agent data (sessions, memory, vector store, transcripts).
    /// Default: <c>AppContext.BaseDirectory/.eca-data</c>.
    /// </summary>
    public AgentConfigBuilder WorkingDirectory(string dir) { _workingDir = dir; return this; }

    /// <summary>Enable FAISS vector memory (semantic search). Default: false.</summary>
    public AgentConfigBuilder EnableVectorMemory(bool enabled = true) { _enableVectorMemory = enabled; return this; }

    /// <summary>Enable sub-agents (parallel child agents). Default: false.</summary>
    public AgentConfigBuilder EnableSubAgents(bool enabled = true) { _enableSubAgents = enabled; return this; }

    /// <summary>Enable secondary model for summarization/decomposition. Default: false.</summary>
    public AgentConfigBuilder EnableSecondaryModel(bool enabled = true) { _enableSecondaryModel = enabled; return this; }

    /// <summary>Path to secondary model GGUF (requires EnableSecondaryModel).</summary>
    public AgentConfigBuilder WithSecondaryModel(string path) { _secondaryModelPath = path; return this; }

    /// <summary>
    /// Disable auto-generating appsettings.json on first run.
    /// By default, Build() creates the JSON file if it doesn't exist.
    /// Set to false if you want config purely in code (no JSON file).
    /// </summary>
    public AgentConfigBuilder WithoutJsonFile() { _autoSaveJson = false; return this; }

    /// <summary>
    /// Build the EAgentConfig.
    ///
    /// Flow:
    /// 1. Resolve working directory (default: .eca-data next to executable)
    /// 2. If appsettings.json exists in working dir → load it
    /// 3. Apply code-level overrides (any WithX() calls) on top of JSON values
    /// 4. If no JSON exists and auto-save is on → generate it
    /// </summary>
    public EAgentConfig Build()
    {
        // 1. Resolve working directory
        var workingDir = _workingDir ?? Path.Combine(AppContext.BaseDirectory, ".eca-data");
        Directory.CreateDirectory(workingDir);

        var jsonPath = Path.Combine(workingDir, "appsettings.json");

        // 2. Load existing JSON if it exists
        EAgentConfig config;
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        if (File.Exists(jsonPath))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                config = JsonSerializer.Deserialize<EAgentConfig>(json, jsonOptions) ?? new EAgentConfig();
            }
            catch
            {
                // Corrupt JSON → start fresh with defaults
                config = new EAgentConfig();
            }
        }
        else
        {
            config = new EAgentConfig();
        }

        // 3. Apply code-level overrides (code wins over JSON)
        config.RootPath = workingDir;
        if (config.AgentSettings == null) config.AgentSettings = new AgentConfig();
        config.AgentSettings.WorkingDirectory = workingDir;

        if (!string.IsNullOrEmpty(_modelPath)) config.Llm.ModelPath = _modelPath;
        if (_contextSize.HasValue) config.Llm.ContextSize = _contextSize.Value;
        if (_gpuLayers.HasValue) config.Llm.GpuLayers = _gpuLayers.Value;
        if (_threads.HasValue) config.Llm.Threads = _threads.Value;
        if (_maxTokens.HasValue) config.Inference.MaxTokens = _maxTokens.Value;
        if (_temperature.HasValue) config.Sampling.Temperature = _temperature.Value;
        if (_topP.HasValue) config.Sampling.TopP = _topP.Value;
        if (_topK.HasValue) config.Sampling.TopK = _topK.Value;
        if (_repeatPenalty.HasValue) config.Sampling.RepeatPenalty = _repeatPenalty.Value;
        if (_enableVectorMemory.HasValue) config.VectorMemory.Enabled = _enableVectorMemory.Value;
        if (_enableSubAgents.HasValue) config.SubAgent.Enabled = _enableSubAgents.Value;
        if (_enableSecondaryModel.HasValue) config.SecondaryModel.Enabled = _enableSecondaryModel.Value;
        if (!string.IsNullOrEmpty(_secondaryModelPath)) config.SecondaryModel.ModelPath = _secondaryModelPath;

        // 4. Auto-generate JSON on first run
        if (_autoSaveJson && !File.Exists(jsonPath))
        {
            var json = JsonSerializer.Serialize(config, jsonOptions);
            File.WriteAllText(jsonPath, json);
        }

        return config;
    }
}