using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Services;
using ECAssistant.Session;
using ECAssistant.Tools;
using ECAssistant.Tools.Background;
using ECAssistant.Tools.Build;
using ECAssistant.Tools.Code;
using ECAssistant.Tools.Git;
using ECAssistant.Tools.Reader;
using ECAssistant.Tools.Research;
using ECAssistant.Tools.Shell;
using ECAssistant.Tools.Web;
using ECAssistant.Interfaces;

namespace ECAssistant;

/// <summary>
/// Builder for creating and initializing AgentSessions with standard tools.
///
/// This extracts the session initialization logic from Program.cs into a reusable
/// public API. Library consumers (e.g., ECSQL) use this to create sessions with
/// the built-in tool set, then add their own custom tools on top.
///
/// Usage:
///   var builder = new SessionBuilder(config, workingDir, logger);
///   var session = await builder.BuildSessionAsync(sessionKey, sharedWeights, sharedModelParams, inferenceParams);
///   session.AddListener(myUiListener);
///   session.RegisterTool(new MyCustomTool());
///   session.Prompt("do something");
///
/// To skip built-in tools entirely (only your custom tools):
///   var builder = new SessionBuilder(config, workingDir, logger) { RegisterBuiltInTools = false };
/// </summary>
public class SessionBuilder
{
    private readonly EAgentConfig _config;
    private readonly string _workingDir;
    private readonly string _userConfigDir;
    private readonly ILogger _logger;
    private readonly BackgroundProcessManager _bgManager;

    /// <summary>
    /// Whether to register the built-in ECAssistant tools (shell, file reader, git, etc.).
    /// Default: true. Set to false if you want ONLY your custom tools.
    /// </summary>
    public bool RegisterBuiltInTools { get; set; } = true;

    /// <summary>
    /// Whether to initialize vector memory (FAISS semantic search).
    /// Default: follows config (config.VectorMemory.Enabled).
    /// </summary>
    public bool? EnableVectorMemory { get; set; }

    /// <summary>
    /// Whether to initialize sub-agents.
    /// Default: follows config (config.SubAgent.Enabled).
    /// </summary>
    public bool? EnableSubAgents { get; set; }

    /// <summary>
    /// Whether to initialize the secondary model (for summarization/decomposition).
    /// Default: follows config (config.SecondaryModel.Enabled).
    /// </summary>
    public bool? EnableSecondaryModel { get; set; }

    /// <summary>
    /// The background process manager shared across sessions.
    /// Created automatically if not provided.
    /// </summary>
    public BackgroundProcessManager BackgroundManager => _bgManager;

    /// <summary>
    /// Create a SessionBuilder.
    /// </summary>
    /// <param name="config">Loaded EAgentConfig from appsettings.json</param>
    /// <param name="workingDir">Working directory for the agent</param>
    /// <param name="userConfigDir">User config directory (for resolving relative model paths)</param>
    /// <param name="logger">Logger instance (optional, creates default if null)</param>
    /// <param name="bgManager">Background process manager (optional, creates one if null)</param>
    public SessionBuilder(
        EAgentConfig config,
        string workingDir,
        string userConfigDir,
        ILogger? logger = null,
        BackgroundProcessManager? bgManager = null)
    {
        _config = config;
        _workingDir = workingDir;
        _userConfigDir = userConfigDir;
        _logger = logger ?? new Logger();
        _bgManager = bgManager ?? new BackgroundProcessManager();
    }

    /// <summary>
    /// Build a fully initialized session with standard tools + optional secondary model.
    /// The caller is responsible for creating the AgentSession (e.g., via SessionManager)
    /// and passing it in. This method handles tool registration, vector memory, etc.
    /// </summary>
    public async Task BuildAsync(AgentSession session)
    {
        // ── Vector Memory (semantic search) ──
        if (EnableVectorMemory ?? _config.VectorMemory.Enabled)
        {
            var vecDir = Path.Combine(_workingDir, _config.VectorMemory.Directory);
            await session.InitializeVectorMemoryAsync(vecDir);
        }

        // ── Project Context Manager ──
        await session.InitializeProjectContextAsync();

        // ── Register built-in tools ──
        if (RegisterBuiltInTools)
        {
            RegisterBuiltInToolsAsync(session);
        }

        // ── Secondary Model (optional) ──
        if (EnableSecondaryModel ?? (_config.SecondaryModel.Enabled && !string.IsNullOrEmpty(_config.SecondaryModel.ModelPath)))
        {
            var secondary = LoadSecondaryModel();
            if (secondary != null)
            {
                session.SetSecondaryModel(secondary);
            }
        }

        // ── Sub-agents (only if enabled) ──
        if (EnableSubAgents ?? _config.SubAgent.Enabled)
        {
            await session.InitializeSubAgentsAsync();
        }
    }

    /// <summary>
    /// Register all built-in ECAssistant tools on the session.
    /// Called automatically by BuildAsync unless RegisterBuiltInTools is false.
    /// Can also be called directly if you want to register built-in tools
    /// but control the order or interleave with custom tool registration.
    /// </summary>
    public void RegisterBuiltInToolsAsync(AgentSession session)
    {
        var fileSystem = new FileSystemAdapter();
        var processRunner = new ProcessRunner();
        var configPath = Path.Combine(_userConfigDir, "appsettings.json");
        var configProvider = new ConfigProvider(fileSystem, configPath);
        var httpClient = new HttpClientAdapter();

        session.RegisterTool(new EShellAgent(processRunner, configProvider, _workingDir));
        session.RegisterTool(new EBackgroundExecTool(_bgManager, processRunner, fileSystem, configProvider));
        session.RegisterTool(new EWebSearchTool(httpClient, configProvider));
        session.RegisterTool(new EDotnetBuildTool(processRunner, configProvider));
        session.RegisterTool(new EGitTool(processRunner, fileSystem, configProvider));
        session.RegisterTool(new ECodeEditorTool(fileSystem, configProvider));
        session.RegisterTool(new EFileReaderTool(fileSystem, configProvider));
        session.RegisterTool(new EWebFetchTool(httpClient, configProvider));
        session.RegisterTool(new EFileResearchTool(fileSystem, configProvider));
    }

    /// <summary>
    /// Load the secondary model from config.
    /// Returns null if model file not found or loading fails.
    /// </summary>
    private SecondaryModelLoader? LoadSecondaryModel()
    {
        var secPath = _config.SecondaryModel.ModelPath;
        if (string.IsNullOrEmpty(secPath)) return null;

        if (!Path.IsPathRooted(secPath))
        {
            var secInWork = Path.Combine(_userConfigDir, secPath);
            var secInBuild = Path.Combine(AppContext.BaseDirectory, secPath);
            secPath = File.Exists(secInWork) ? secInWork : (File.Exists(secInBuild) ? secInBuild : secInWork);
        }

        return SecondaryModelLoader.Load(secPath,
            contextSize: _config.SecondaryModel.ContextSize,
            gpuLayers: _config.SecondaryModel.GpuLayers,
            temperature: _config.SecondaryModel.Temperature,
            topP: _config.SecondaryModel.TopP,
            topK: _config.SecondaryModel.TopK,
            repeatPenalty: _config.SecondaryModel.RepeatPenalty,
            maxTokens: _config.SecondaryModel.MaxTokens,
            antiPrompts: _config.SecondaryModel.AntiPrompts,
            logger: _logger);
    }
}