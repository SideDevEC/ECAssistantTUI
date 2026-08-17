using ECAssistant.Core;
using System.Text.Json;
using ECAssistant.Core.Config;

namespace ECAssistant.TUI.UI;

/// <summary>
/// Config layer — displays all EAgentConfig values in a readable format.
/// Read-only inspection. Accessible via /config command.
/// </summary>
public sealed class ConfigLayer : BaseLayer
{
    private readonly EColor _color;
    
    public override string Name => "config";
    
    public ConfigLayer(EColor color)
    {
        _color = color;
    }
    
    /// <summary>Build the config display from an EAgentConfig instance.</summary>
    public void BuildFromConfig(EAgentConfig config, string modelPath)
    {
        _outputLines.Clear();
        _scrollOffset = 0;
        
        _outputLines.Add($"{_color.Cyan}{_color.Bold}  ECAssistant — Configuration{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  ════════════════════════════════════════{_color.Reset}");
        _outputLines.Add("");
        
        // ── LLM Settings ──
        _outputLines.Add($"{_color.Yellow}{_color.Bold}  LLM{_color.Reset}");
        AddConfigProperty("Model", config.Llm.ModelPath);
        AddConfigProperty("Resolved", modelPath);
        AddConfigProperty("Context Size", config.Llm.ContextSize);
        AddConfigProperty("GPU Layers", config.Llm.GpuLayers);
        AddConfigProperty("Threads", config.Llm.Threads);
        
        // Try to get Temperature and other optional LLM properties via reflection
        TryAddProperty(config.Llm, "Temperature");
        TryAddProperty(config.Llm, "TopP");
        TryAddProperty(config.Llm, "RepeatPenalty");
        
        // Background tasks (replaces secondary model in v11.2+)
        bool decomposeLlm = config.BackgroundTasks?.Decompose?.UseLlm ?? false;
        bool summarizeLlm = config.BackgroundTasks?.Summarize?.UseLlm ?? false;
        string bgStatus = (decomposeLlm || summarizeLlm) ? $"decompose:{(decomposeLlm ? "LLM" : "keyword")} summarize:{(summarizeLlm ? "LLM" : "extractive")}" : "disabled";
        
        _outputLines.Add($"{_color.Cyan}  Bg Tasks:     {_color.Reset}{(decomposeLlm || summarizeLlm ? bgStatus : $"{_color.Dim}disabled{_color.Reset}")}");
        _outputLines.Add("");
        
        // ── Agent Settings ──
        _outputLines.Add($"{_color.Yellow}{_color.Bold}  Agent{_color.Reset}");
        AddConfigProperty("Working Dir", config.AgentSettings.WorkingDirectory);
        AddConfigProperty("Root Path", config.RootPath);
        _outputLines.Add("");
        
        // ── Memory ──
        _outputLines.Add($"{_color.Yellow}{_color.Bold}  Memory{_color.Reset}");
        AddConfigProperty("Data Path", config.Memory.DataPath);
        _outputLines.Add("");
        
        // ── Workspace ──
        _outputLines.Add($"{_color.Yellow}{_color.Bold}  Workspace{_color.Reset}");
        AddConfigProperty("Path", config.Workspace.Path);
        _outputLines.Add("");
        
        // ── Tools ──
        _outputLines.Add($"{_color.Yellow}{_color.Bold}  Tools{_color.Reset}");
        try
        {
            var toolsProp = config.GetType().GetProperty("Tools");
            if (toolsProp != null)
            {
                var toolsConfig = toolsProp.GetValue(config);
                if (toolsConfig != null)
                {
                    var toolsListProp = toolsConfig.GetType().GetProperty("Tools");
                    if (toolsListProp != null)
                    {
                        var toolsList = toolsListProp.GetValue(toolsConfig) as System.Collections.IEnumerable;
                        if (toolsList != null)
                        {
                            foreach (var tool in toolsList)
                            {
                                var nameProp = tool.GetType().GetProperty("Name");
                                var enabledProp = tool.GetType().GetProperty("Enabled");
                                string name = nameProp?.GetValue(tool)?.ToString() ?? "?";
                                bool enabled = (bool)(enabledProp?.GetValue(tool) ?? false);
                                _outputLines.Add($"{_color.Cyan}  {name,-16}{_color.Reset}{(enabled ? "enabled" : $"{_color.Dim}disabled{_color.Reset}")}");
                            }
                        }
                    }
                }
            }
        }
        catch { }
        _outputLines.Add("");
        
        // ── SubAgent ──
        _outputLines.Add($"{_color.Yellow}{_color.Bold}  SubAgent{_color.Reset}");
        try
        {
            var subAgentProp = config.GetType().GetProperty("SubAgent");
            if (subAgentProp != null)
            {
                var subAgent = subAgentProp.GetValue(config);
                if (subAgent != null)
                {
                    TryAddPropertyFromObject(subAgent, "Enabled");
                    TryAddPropertyFromObject(subAgent, "ModelPath");
                    TryAddPropertyFromObject(subAgent, "MaxParallel");
                    TryAddPropertyFromObject(subAgent, "MaxTurns");
                }
            }
        }
        catch { }
        _outputLines.Add("");
        
        // ── Footer ──
        _outputLines.Add($"{_color.Dim}  ────────────────────────────────────────{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  Press Enter or ESC to return{_color.Reset}");
        
        _isDirty = true;
        RequestRepaint();
    }
    
    private void AddConfigProperty(string label, object? value)
    {
        _outputLines.Add($"{_color.Cyan}  {label,-16}{_color.Reset}{value ?? "(null)"}");
    }
    
    private void TryAddProperty(object obj, string propName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop != null)
                AddConfigProperty(propName, prop.GetValue(obj));
        }
        catch { }
    }
    
    private void TryAddPropertyFromObject(object obj, string propName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop != null)
                AddConfigProperty(propName, prop.GetValue(obj));
        }
        catch { }
    }
    
    public override bool ProcessInput(string input)
    {
        // Let base handle common commands (/clear)
        if (base.ProcessInput(input)) return true;
        // Any other input → signal return (controller pops layer)
        return false;
    }
}