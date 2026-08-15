using ECAssistant.Config;

namespace ECAssistant;

/// <summary>
/// Fluent config builder for library consumers.
///
/// Provides a simple API to configure ECAssistant.Core without needing
/// appsettings.json or knowing the full EAgentConfig structure.
///
/// Usage:
///   var config = AgentConfigBuilder.Create()
///       .WithModel("/path/to/model.gguf")
///       .ContextSize(16384)
///       .GpuLayers(15)
///       .Temperature(0.3)
///       .WorkingDirectory("/my/project")
///       .EnableSubAgents(true)
///       .Build();
///
///   var sessionManager = new SessionManager(config, modelPath, workingDir, logger);
///   var session = sessionManager.Main;
///   var builder = new SessionBuilder(config, workingDir, userConfigDir, logger);
///   await builder.BuildAsync(session);
///
/// Only the essential settings are exposed. The full EAgentConfig is still
/// available for advanced users who need every knob.
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

    /// <summary>Working directory for the agent. Default: current directory.</summary>
    public AgentConfigBuilder WorkingDirectory(string dir) { _workingDir = dir; return this; }

    /// <summary>Enable FAISS vector memory (semantic search). Default: false.</summary>
    public AgentConfigBuilder EnableVectorMemory(bool enabled = true) { _enableVectorMemory = enabled; return this; }

    /// <summary>Enable sub-agents (parallel child agents). Default: false.</summary>
    public AgentConfigBuilder EnableSubAgents(bool enabled = true) { _enableSubAgents = enabled; return this; }

    /// <summary>Enable secondary model for summarization/decomposition. Default: false.</summary>
    public AgentConfigBuilder EnableSecondaryModel(bool enabled = true) { _enableSecondaryModel = enabled; return this; }

    /// <summary>Path to secondary model GGUF (requires EnableSecondaryModel).</summary>
    public AgentConfigBuilder WithSecondaryModel(string path) { _secondaryModelPath = path; return this; }

    /// <summary>Build the EAgentConfig.</summary>
    public EAgentConfig Build()
    {
        return new EAgentConfig
        {
            RootPath = ".",
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
    }
}