using ECAssistant.Core;
using ECAssistant.Core.Config;

namespace ECAssistant.TUI.UI;

/// <summary>
/// Config layer — displays all AppConfig values in a readable format.
/// Read-only inspection. Accessible via /config command.
/// Uses the typed AppConfig surface directly (no reflection).
/// </summary>
public sealed class ConfigLayer : BaseLayer
{
    private readonly AnsiColor _color;
    
    public override string Name => "config";
    
    public ConfigLayer(AnsiColor color)
    {
        _color = color;
    }
    
    /// <summary>Build the config display from an AppConfig instance.</summary>
    public void BuildFromConfig(AppConfig config, string modelPath)
    {
        Clear();
        
        AddOutputLine($"{_color.Cyan}{_color.Bold}  ECAssistant — Configuration{_color.Reset}");
        AddOutputLine($"{_color.Dim}  ════════════════════════════════════════{_color.Reset}");
        AddOutputLine("");
        
        // ── LLM Settings ──
        AddOutputLine($"{_color.Yellow}{_color.Bold}  LLM{_color.Reset}");
        AddConfigProperty("Model", config.Llm.ModelPath);
        AddConfigProperty("Resolved", modelPath);
        AddConfigProperty("Context Size", config.Llm.ContextSize);
        
        // ── LLM Provider (v10.30: HTTP-based inference) ──
        AddOutputLine("");
        AddOutputLine($"{_color.Yellow}{_color.Bold}  LLM Provider{_color.Reset}");
        AddConfigProperty("Mode", config.LlmProvider.Mode);
        AddConfigProperty("Vision", config.SupportsVision ? "enabled" : "disabled");
        AddConfigProperty("Endpoint", config.LlmProvider.Endpoint);
        AddConfigProperty("Model ID", config.LlmProvider.ModelId);
        if (!string.IsNullOrEmpty(config.LlmProvider.EmbeddingModelId))
            AddConfigProperty("Embedding Model", config.LlmProvider.EmbeddingModelId);
        if (config.LlmProvider.IsLocal)
        {
            AddConfigProperty("Auto-Start", config.LlmProvider.AutoStart);
            AddConfigProperty("Heartbeat", $"{config.LlmProvider.HeartbeatIntervalSec}s");
        }
        if (config.LlmProvider.IsRemote && !string.IsNullOrEmpty(config.LlmProvider.ApiKey))
            AddConfigProperty("API Key", "***configured***");
        
        // Background tasks (replaces secondary model in v11.2+)
        bool decomposeLlm = config.BackgroundTasks?.Decompose?.UseLlm ?? false;
        bool summarizeLlm = config.BackgroundTasks?.Summarize?.UseLlm ?? false;
        string bgStatus = (decomposeLlm || summarizeLlm) ? $"decompose:{(decomposeLlm ? "LLM" : "keyword")} summarize:{(summarizeLlm ? "LLM" : "extractive")}" : "disabled";
        
        AddOutputLine($"{_color.Cyan}  Bg Tasks:     {_color.Reset}{(decomposeLlm || summarizeLlm ? bgStatus : $"{_color.Dim}disabled{_color.Reset}")}");
        AddOutputLine("");
        
        // ── Agent Settings ──
        AddOutputLine($"{_color.Yellow}{_color.Bold}  Agent{_color.Reset}");
        AddConfigProperty("Working Dir", config.AgentSettings.WorkingDirectory);
        AddConfigProperty("Root Path", config.RootPath);
        AddOutputLine("");
        
        // ── Memory ──
        AddOutputLine($"{_color.Yellow}{_color.Bold}  Memory{_color.Reset}");
        AddConfigProperty("Data Path", config.Memory.DataPath);
        AddOutputLine("");
        
        // ── Workspace ──
        AddOutputLine($"{_color.Yellow}{_color.Bold}  Workspace{_color.Reset}");
        AddConfigProperty("Path", config.Workspace.Path);
        AddOutputLine("");
        
        // ── Tools ──
        AddOutputLine($"{_color.Yellow}{_color.Bold}  Tools{_color.Reset}");
        var systemTools = config.SystemTools;
        if (systemTools is { Count: > 0 })
        {
            foreach (var tool in systemTools)
            {
                AddOutputLine($"{_color.Cyan}  {tool.ToolName,-16}{_color.Reset}{(tool.ApprovalRequired ? "approval required" : $"{_color.Dim}auto-approved{_color.Reset}")}");
            }
        }
        else
        {
            AddOutputLine($"{_color.Dim}  (default tool policy — no overrides configured){_color.Reset}");
        }
        AddOutputLine("");
        
        // ── SubAgent ──
        AddOutputLine($"{_color.Yellow}{_color.Bold}  SubAgent{_color.Reset}");
        var subAgent = config.SubAgent;
        if (subAgent != null)
        {
            AddConfigProperty("Enabled", subAgent.Enabled);
            AddConfigProperty("MaxConcurrent", subAgent.MaxConcurrent);
            AddConfigProperty("MaxTurns", subAgent.MaxTurns);
            AddConfigProperty("TimeoutSec", subAgent.TimeoutSeconds);
        }
        AddOutputLine("");
        
        // ── Footer ──
        AddOutputLine($"{_color.Dim}  ────────────────────────────────────────{_color.Reset}");
        AddOutputLine($"{_color.Dim}  Press ESC to return{_color.Reset}");
    }
    
    private void AddConfigProperty(string label, object? value)
    {
        AddOutputLine($"{_color.Cyan}  {label,-16}{_color.Reset}{value ?? "(null)"}");
    }
    
    public override bool ProcessInput(string input)
    {
        // Let base handle common commands (/clear)
        if (base.ProcessInput(input)) return true;
        // Any other input → signal return (controller pops layer)
        return false;
    }
}
