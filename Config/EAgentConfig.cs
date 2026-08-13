using ECAssistant;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECAssistant.UI;

namespace ECAssistant.Config;

public class LlmConfig
{
     [JsonPropertyName("model_path")]
    public string ModelPath { get; set; } = "Qwen3-8B-Q4_K_M.gguf";
     [JsonPropertyName("context_size")]
    public uint ContextSize { get; set; } = 16384;
     [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; set; } = 15;
     [JsonPropertyName("threads")]
    public int Threads { get; set; } = -1;
     [JsonPropertyName("batch_size")]
    public uint BatchSize { get; set; } = 256;
     [JsonPropertyName("ubatch_size")]
    public uint UBatchSize { get; set; } = 128;
}

public class SecondaryModelConfig
{
     [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;
     [JsonPropertyName("model_path")]
    public string ModelPath { get; set; } = "";
     [JsonPropertyName("context_size")]
    public uint ContextSize { get; set; } = 4096;
     [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; set; } = 0;
     [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.1f;
     [JsonPropertyName("top_p")]
    public float TopP { get; set; } = 0.8f;
     [JsonPropertyName("top_k")]
    public int TopK { get; set; } = 40;
     [JsonPropertyName("repeat_penalty")]
    public float RepeatPenalty { get; set; } = 1.1f;
     [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 512;
     [JsonPropertyName("anti_prompts")]
    public string[] AntiPrompts { get; set; } = new[] { "User:", "Question:", "\n```,\n" };
}

public class VectorMemoryConfig
{
     [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
     [JsonPropertyName("directory")]
    public string Directory { get; set; } = "vecmem";
     [JsonPropertyName("max_results")]
    public int MaxResults { get; set; } = 5;
     [JsonPropertyName("auto_index")]
    public bool AutoIndex { get; set; } = true;
}

public class InferenceConfig
{
     [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 8192;
     [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.3f;
     [JsonPropertyName("top_p")]
    public float TopP { get; set; } = 0.9f;
     [JsonPropertyName("repeat_penalty")]
    public float RepeatPenalty { get; set; } = 1.1f;
     [JsonPropertyName("anti_prompts")]
    public string[] AntiPrompts { get; set; } = new[] {
        "</s>",
        "\n```\n",
        "User:",
        "Question:"
    };
     [JsonPropertyName("tokens_keep")]
    public int TokensKeep { get; set; } = 0;
     [JsonPropertyName("overflow_strategy")]
    public string OverflowStrategy { get; set; } = "ThrowException";
     [JsonPropertyName("decode_special_tokens")]
    public bool DecodeSpecialTokens { get; set; } = false;
}

public class ContextManagementConfig
{
     [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "SummaryAndShift";
     [JsonPropertyName("shift_at_messages")]
    public int ShiftAtMessages { get; set; } = 40;
     [JsonPropertyName("keep_last")]
    public int KeepLast { get; set; } = 20;
     [JsonPropertyName("summarize_prompt")]
    public string SummarizePrompt { get; set; } = "Summarize the key decisions, actions taken, and current state of our conversation. Keep it concise but include any important findings or tool results that will help me continue this task.";
     [JsonPropertyName("max_summary_length")]
    public int MaxSummaryLength { get; set; } = 2000;
     [JsonPropertyName("auto_shift_on_generate")]
    public bool AutoShiftOnGenerate { get; set; } = true;
     [JsonPropertyName("shift_guardrail_threshold")]
    public int ShiftGuardrailThreshold { get; set; } = 3;
}

public class SamplingConfig
{
     [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.3f;
     [JsonPropertyName("top_p")]
    public float TopP { get; set; } = 0.9f;
     [JsonPropertyName("top_k")]
    public int TopK { get; set; } = 40;
     [JsonPropertyName("repeat_penalty")]
    public float RepeatPenalty { get; set; } = 1.1f;
     [JsonPropertyName("penalty_last_n")]
    public int PenaltyLastN { get; set; } = 64;
     [JsonPropertyName("mirostat")]
    public bool Mirostat { get; set; } = false;
     [JsonPropertyName("mirostat_tau")]
    public float MirostatTau { get; set; } = 5.0f;
     [JsonPropertyName("mirostat_eta")]
    public float MirostatEta { get; set; } = 0.1f;
}

public class MemoryConfig
{
     [JsonPropertyName("data_path")]
    public string DataPath { get; set; } = "Memory";
     [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
     [JsonPropertyName("load_on_start")]
    public bool LoadOnStart { get; set; } = true;
     [JsonPropertyName("save_on_exit")]
    public bool SaveOnExit { get; set; } = true;
     [JsonPropertyName("max_entries")]
    public int MaxEntries { get; set; } = 500;
     [JsonPropertyName("auto_backup")]
    public bool AutoBackup { get; set; } = true;
}

public class WorkspaceConfig
{
     [JsonPropertyName("path")]
    public string Path { get; set; } = "Workspace";
     [JsonPropertyName("allow_delete")]
    public bool AllowDelete { get; set; } = true;
     [JsonPropertyName("max_size_mb")]
    public int MaxSizeMB { get; set; } = 500;
     [JsonPropertyName("auto_cleanup")]
    public bool AutoCleanup { get; set; } = true;
}

public class ToolPowerShellConfig
{
     [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
     [JsonPropertyName("use_pwsh_core")]
    public bool UsePwshCore { get; set; } = true;
     [JsonPropertyName("fallback_to_powershell_exe")]
    public bool FallbackToPowerShellExe { get; set; } = true;
     [JsonPropertyName("max_output_chars")]
    public int MaxOutputChars { get; set; } = 50000;
}

public class ToolFileResearchConfig
{
     [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
     [JsonPropertyName("default_extensions")]
    public List<string> DefaultExtensions { get; set; } = new() {
        ".cs", ".md", ".json", ".txt", ".xml", ".ps1", ".sln", ".csproj", ".config", ".sql", ".html", ".css", ".js"
    };
     [JsonPropertyName("max_chars_per_file")]
    public int MaxCharsPerFile { get; set; } = 15000;
     [JsonPropertyName("max_files_to_scan")]
    public int MaxFilesToScan { get; set; } = 50;
     [JsonPropertyName("query_limit")]
    public int QueryLimit { get; set; } = 20;
}

public class ToolsConfig
{
     [JsonPropertyName("e_power_shell_agent")]
    public ToolPowerShellConfig EShellAgent { get; set; } = new();
     [JsonPropertyName("e_file_research_tool")]
    public ToolFileResearchConfig EFileResearchTool { get; set; } = new();
}

public class AgentConfig
{
     [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = "."; // "." = ~/ECAssistant (auto-resolved at startup)
     [JsonPropertyName("allow_delete")]
    public bool AllowDelete { get; set; } = true;
     [JsonPropertyName("allowed_extensions")]
    public List<string> AllowedExtensions { get; set; } = new() { ".txt", ".json", ".md", ".cs", ".py" };
}

public class InterfaceConfig
{
     [JsonPropertyName("history_max_messages")]
    public int HistoryMaxMessages { get; set; } = 50;
     [JsonPropertyName("show_elapsed_time")]
    public bool ShowElapsedTime { get; set; } = true;
     [JsonPropertyName("prompt_prefix")]
    public string PromptPrefix { get; set; } = "[You]: ";
     [JsonPropertyName("response_prefix")]
    public string ResponsePrefix { get; set; } = "[Agent]: ";
     [JsonPropertyName("auto_clear_history_after")]
    public object? AutoClearHistoryAfter { get; set; } = null;
}

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

    public static EAgentConfig Load(string filePath = "appsettings.json")
     {
        try
         {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<EAgentConfig>(json);
                if (config != null) return config;
                Program.Gui.InfoColored($"[!] Config parse returned null — using defaults.");
            }
            else
            {
                Program.Gui.InfoColored($"[!] Config not found: {filePath} — using defaults.");
            }
         }
        catch (Exception ex)
         {
            Program.Gui.InfoColored($"[!] Config load error: {ex.Message} — using defaults.");
         }
        return new EAgentConfig();
     }

    public void Save(string filePath = "appsettings.json")
     {
        File.WriteAllText(filePath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
     }
}
