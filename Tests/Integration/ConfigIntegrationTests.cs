using ECAssistant.Config;
using ECAssistant.Services;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Integration;

/// <summary>
/// Integration tests for the config loading pipeline — uses real FileSystemAdapter
/// and real ConfigLoader/ConfigProvider with temp file system I/O.
/// </summary>
public class ConfigIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAInteg_Config_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string WriteAppSettings(string json)
    {
        var path = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string GetFullPath(string fileName)
        => Path.Combine(_tempDir, fileName);

    [Fact]
    public void ConfigLoader_RealFileSystem_LoadsAllSectionsCorrectly()
    {
        var json = """
        {
          "root_path": "MyTestProject",
          "memory": {
            "data_path": "CustomMemory",
            "enabled": false,
            "load_on_start": false,
            "save_on_exit": true,
            "max_entries": 100,
            "auto_backup": false
          },
          "workspace": {
            "path": "CustomWorkspace",
            "allow_delete": false,
            "max_size_mb": 200,
            "auto_cleanup": false
          },
          "llm": {
            "model_path": "model.gguf",
            "context_size": 8192,
            "gpu_layers": 10,
            "threads": 4,
            "batch_size": 128,
            "ubatch_size": 64
          },
          "sampling": {
            "temperature": 0.5,
            "top_p": 0.85,
            "top_k": 20,
            "repeat_penalty": 1.2
          },
          "inference": {
            "max_tokens": 4096,
            "temperature": 0.2,
            "overflow_strategy": "Truncate"
          },
          "context_management": {
            "strategy": "SlidingWindow",
            "shift_at_messages": 20,
            "keep_last": 10
          },
          "agent": {
            "working_directory": "/tmp/work",
            "allow_delete": false,
            "allowed_extensions": [".cs", ".py"]
          },
          "interface": {
            "history_max_messages": 100,
            "show_elapsed_time": false,
            "prompt_prefix": ">> "
          }
        }
        """;
        var path = WriteAppSettings(json);

        var fs = new FileSystemAdapter();
        var loader = new ConfigLoader(fs);
        var config = loader.Load(path);

        // Root
        Assert.Equal("MyTestProject", config.RootPath);

        // Memory section
        Assert.Equal("CustomMemory", config.Memory.DataPath);
        Assert.False(config.Memory.Enabled);
        Assert.False(config.Memory.LoadOnStart);
        Assert.True(config.Memory.SaveOnExit);
        Assert.Equal(100, config.Memory.MaxEntries);
        Assert.False(config.Memory.AutoBackup);

        // Workspace section
        Assert.Equal("CustomWorkspace", config.Workspace.Path);
        Assert.False(config.Workspace.AllowDelete);
        Assert.Equal(200, config.Workspace.MaxSizeMB);
        Assert.False(config.Workspace.AutoCleanup);

        // LLM section
        Assert.Equal("model.gguf", config.Llm.ModelPath);
        Assert.Equal(8192u, config.Llm.ContextSize);
        Assert.Equal(10, config.Llm.GpuLayers);
        Assert.Equal(4, config.Llm.Threads);

        // Sampling section
        Assert.Equal(0.5f, config.Sampling.Temperature);
        Assert.Equal(0.85f, config.Sampling.TopP);
        Assert.Equal(20, config.Sampling.TopK);
        Assert.Equal(1.2f, config.Sampling.RepeatPenalty);

        // Inference section
        Assert.Equal(4096, config.Inference.MaxTokens);
        Assert.Equal(0.2f, config.Inference.Temperature);
        Assert.Equal("Truncate", config.Inference.OverflowStrategy);

        // Context management
        Assert.Equal("SlidingWindow", config.ContextManagement.Strategy);
        Assert.Equal(20, config.ContextManagement.ShiftAtMessages);
        Assert.Equal(10, config.ContextManagement.KeepLast);

        // Agent section
        Assert.Equal("/tmp/work", config.AgentSettings.WorkingDirectory);
        Assert.False(config.AgentSettings.AllowDelete);
        Assert.Equal(2, config.AgentSettings.AllowedExtensions.Count);
        Assert.Contains(".cs", config.AgentSettings.AllowedExtensions);
        Assert.Contains(".py", config.AgentSettings.AllowedExtensions);

        // Interface section
        Assert.Equal(100, config.Interface.HistoryMaxMessages);
        Assert.False(config.Interface.ShowElapsedTime);
        Assert.Equal(">> ", config.Interface.PromptPrefix);
    }

    [Fact]
    public void ConfigLoader_MissingFile_ReturnsDefaultConfig()
    {
        var fs = new FileSystemAdapter();
        var loader = new ConfigLoader(fs);
        var config = loader.Load(GetFullPath("nonexistent.json"));

        Assert.NotNull(config);
        Assert.Equal("ECAssistant", config.RootPath);
        // Verify default sections are initialized
        Assert.NotNull(config.Memory);
        Assert.NotNull(config.Llm);
        Assert.NotNull(config.Workspace);
        Assert.True(config.Memory.Enabled); // default
        Assert.Equal(16384u, config.Llm.ContextSize); // default
    }

    [Fact]
    public void ConfigLoader_MalformedJson_ReturnsDefaultsWithoutCrash()
    {
        WriteAppSettings("{ this is not valid json {{{ }");

        var fs = new FileSystemAdapter();
        var loader = new ConfigLoader(fs);
        var config = loader.Load(GetFullPath("appsettings.json"));

        Assert.NotNull(config);
        Assert.Equal("ECAssistant", config.RootPath);
    }

    [Fact]
    public void ConfigProvider_RealFileSystem_GetSectionReturnsCorrectValues()
    {
        var json = """
        {
          "root_path": "ProviderTest",
          "memory": {
            "data_path": "ProviderMem",
            "enabled": true,
            "max_entries": 250
          },
          "llm": {
            "model_path": "test.gguf",
            "context_size": 4096
          }
        }
        """;
        var path = WriteAppSettings(json);

        var fs = new FileSystemAdapter();
        var provider = new ConfigProvider(fs, path);

        // GetSection for memory
        var memSection = provider.GetSection<MemoryConfig>("memory");
        Assert.Equal("ProviderMem", memSection.DataPath);
        Assert.True(memSection.Enabled);
        Assert.Equal(250, memSection.MaxEntries);

        // GetSection for llm
        var llmSection = provider.GetSection<LlmConfig>("llm");
        Assert.Equal("test.gguf", llmSection.ModelPath);
        Assert.Equal(4096u, llmSection.ContextSize);
    }

    [Fact]
    public void EAgentConfig_SaveLoad_RoundTrip_PreservesAllValues()
    {
        var config = new EAgentConfig
        {
            RootPath = "RoundTripProject",
            Memory = new MemoryConfig
            {
                DataPath = "RTMemory",
                Enabled = false,
                LoadOnStart = false,
                SaveOnExit = true,
                MaxEntries = 300,
                AutoBackup = false
            },
            Workspace = new WorkspaceConfig
            {
                Path = "RTWorkspace",
                AllowDelete = false,
                MaxSizeMB = 1000,
                AutoCleanup = false
            },
            Llm = new LlmConfig
            {
                ModelPath = "rt_model.gguf",
                ContextSize = 32768,
                GpuLayers = 20,
                Threads = 8,
                BatchSize = 512,
                UBatchSize = 256
            },
            Sampling = new SamplingConfig
            {
                Temperature = 0.7f,
                TopP = 0.95f,
                TopK = 60,
                RepeatPenalty = 1.15f,
                Mirostat = true,
                MirostatTau = 4.0f,
                MirostatEta = 0.2f
            },
            Inference = new InferenceConfig
            {
                MaxTokens = 2048,
                Temperature = 0.1f,
                OverflowStrategy = "Truncate",
                DecodeSpecialTokens = true
            },
            ContextManagement = new ContextManagementConfig
            {
                Strategy = "SlidingWindow",
                ShiftAtMessages = 30,
                KeepLast = 15,
                AutoShiftOnGenerate = false
            },
            AgentSettings = new AgentConfig
            {
                WorkingDirectory = "/rt/work",
                AllowDelete = false,
                AllowedExtensions = new() { ".cs", ".py", ".go" }
            },
            Interface = new InterfaceConfig
            {
                HistoryMaxMessages = 25,
                ShowElapsedTime = false,
                PromptPrefix = "[RT]: ",
                ResponsePrefix = "[AI]: "
            }
        };

        var filePath = Path.Combine(_tempDir, "roundtrip.json");
        config.Save(filePath);

        // Load via ConfigLoader with real FileSystemAdapter
        var fs = new FileSystemAdapter();
        var loader = new ConfigLoader(fs);
        var loaded = loader.Load(filePath);

        // Verify all sections preserved
        Assert.Equal("RoundTripProject", loaded.RootPath);

        Assert.Equal("RTMemory", loaded.Memory.DataPath);
        Assert.False(loaded.Memory.Enabled);
        Assert.Equal(300, loaded.Memory.MaxEntries);

        Assert.Equal("RTWorkspace", loaded.Workspace.Path);
        Assert.False(loaded.Workspace.AllowDelete);
        Assert.Equal(1000, loaded.Workspace.MaxSizeMB);

        Assert.Equal("rt_model.gguf", loaded.Llm.ModelPath);
        Assert.Equal(32768u, loaded.Llm.ContextSize);
        Assert.Equal(20, loaded.Llm.GpuLayers);
        Assert.Equal(8, loaded.Llm.Threads);

        Assert.Equal(0.7f, loaded.Sampling.Temperature);
        Assert.Equal(0.95f, loaded.Sampling.TopP);
        Assert.Equal(60, loaded.Sampling.TopK);
        Assert.True(loaded.Sampling.Mirostat);

        Assert.Equal(2048, loaded.Inference.MaxTokens);
        Assert.Equal("Truncate", loaded.Inference.OverflowStrategy);
        Assert.True(loaded.Inference.DecodeSpecialTokens);

        Assert.Equal("SlidingWindow", loaded.ContextManagement.Strategy);
        Assert.Equal(30, loaded.ContextManagement.ShiftAtMessages);
        Assert.False(loaded.ContextManagement.AutoShiftOnGenerate);

        Assert.Equal("/rt/work", loaded.AgentSettings.WorkingDirectory);
        Assert.False(loaded.AgentSettings.AllowDelete);
        Assert.Equal(3, loaded.AgentSettings.AllowedExtensions.Count);

        Assert.Equal(25, loaded.Interface.HistoryMaxMessages);
        Assert.False(loaded.Interface.ShowElapsedTime);
        Assert.Equal("[RT]: ", loaded.Interface.PromptPrefix);
    }

    [Fact]
    public void ConfigLoader_EmptyJson_ReturnsDefaults()
    {
        WriteAppSettings("");

        var fs = new FileSystemAdapter();
        var loader = new ConfigLoader(fs);
        var config = loader.Load(GetFullPath("appsettings.json"));

        Assert.NotNull(config);
        Assert.Equal("ECAssistant", config.RootPath);
    }

    [Fact]
    public void ConfigLoader_PartialJson_OverridesOnlyProvidedSections()
    {
        // Only root_path and memory provided; everything else should be defaults
        var json = """{"root_path":"PartialTest","memory":{"data_path":"PartialMem"}}""";
        WriteAppSettings(json);

        var fs = new FileSystemAdapter();
        var loader = new ConfigLoader(fs);
        var config = loader.Load(GetFullPath("appsettings.json"));

        Assert.Equal("PartialTest", config.RootPath);
        Assert.Equal("PartialMem", config.Memory.DataPath);
        // Untouched sections should have defaults
        Assert.Equal("Qwen3-8B-Q4_K_M.gguf", config.Llm.ModelPath); // default model path
        Assert.Equal(500, config.Workspace.MaxSizeMB); // default
    }
}