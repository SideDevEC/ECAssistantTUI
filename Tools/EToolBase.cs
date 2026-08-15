using System.Text.Json;
using ECAssistant.Config;

namespace ECAssistant.Tools;

/// <summary>
/// Base class for all Tools. 
/// Each tool must extend this and implement ExecuteAsync().
/// New tools are added by creating a subclass — no changes to Harness required.
/// </summary>
public abstract class EToolBase
{
             /// <summary>Unique name of this tool (e.g., "EShellAgent")</summary>
    public abstract string Name { get; }

          /// <summary>Human-readable description for system prompt injection</summary>
    public abstract string Description { get; }

          /// <summary>Whether this tool is enabled. Read from config.Tools[Name].enabled.</summary>
    public virtual bool IsEnabled { get; protected set; } = true;

               /// <summary>Example usage text shown to the LLM in the system prompt</summary>
    public abstract string UsageExample { get; }

                /// <summary>
                /// Execute the tool with given arguments.
                 /// </summary>
             /// <param name="arguments">Dictionary of argument key/value pairs from the LLM</param>
    public abstract Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// v10.24: Returns the default config section for this tool.
    /// Called when the tool is registered and its config section is not yet in appsettings.json.
    /// Every tool returns at minimum { enabled = true }.
    /// Override to add tool-specific parameters.
    /// </summary>
    public virtual object GetConfigSection() => new { enabled = true };

                /// <summary>
                /// Override to provide additional tool-specific system prompt text.
                /// Returned text is appended to the main system prompt.
                 /// </summary>
          public virtual string GetExtendedSystemPrompt() 
                  => string.Empty;

                   /// <summary>
                    /// Return a one-line XML example showing how to call this tool.
                    /// The LLM sees this next to each tool name in the system prompt.
                    /// Override for useful examples. Default returns nothing.
                     /// </summary>
          public virtual string GetToolExample() 
                   => string.Empty;

                   /// <summary>
                    /// Override to provide strict policy rules for this tool.
                    /// Returned text is injected into the system prompt alongside examples.
                    /// Use for non-negotiable constraints, format requirements, or behavioral rules.
                     /// </summary>
          public virtual string GetToolRules() 
                   => string.Empty;

                   /// <summary>
                    /// One unified block for the LLM: Name then Description then Rules then Examples.
                    /// Override GetToolRules() to add policy constraints on top of existing examples.
                    /// The engine calls this once per tool during registration prompt injection.
                     /// </summary>
          public string ToSystemPromptBlock()
               {
                var sb = new System.Text.StringBuilder();
              sb.AppendLine($"## {Name}");
              sb.AppendLine($"{Description}");

            // Rules come before examples (enforce first, illustrate second)
            var rules = GetToolRules();
            if (!string.IsNullOrEmpty(rules))
               {
                sb.AppendLine();
                sb.Append(rules);
               }

            var example = GetToolExample();
            if (!string.IsNullOrEmpty(example))
               {
                sb.AppendLine();
                sb.Append("Examples:");
                sb.AppendLine();
                sb.Append(example);
               }

            return sb.ToString();
           }

    // ── v10.24: Config helpers ──

    /// <summary>
    /// Read a value from the tool's config section in EAgentConfig.Tools.
    /// Returns defaultValue if the key is not found or the section doesn't exist.
    /// </summary>
    protected static T ReadConfig<T>(Dictionary<string, JsonElement> tools, string toolName, string key, T defaultValue)
    {
        if (tools.TryGetValue(toolName, out var section) && section.ValueKind == JsonValueKind.Object)
        {
            if (section.TryGetProperty(key, out var prop))
            {
                try { return prop.Deserialize<T>() ?? defaultValue; }
                catch { return defaultValue; }
            }
        }
        return defaultValue;
    }

    /// <summary>
    /// Check if a tool is enabled in the config.
    /// </summary>
    protected static bool IsToolEnabled(Dictionary<string, JsonElement> tools, string toolName)
        => ReadConfig(tools, toolName, "enabled", true);
}

/// <summary>
/// Standardized tool call result that flows from any Tool back to the Agent.
/// This is the contract all tools must return through.
/// </summary>
public class EToolResult
{
          /// <summary>Tool name that produced this result</summary>
    public string ToolName { get; init; } = "";

             /// <summary>Success/failure status</summary>
    public bool Succeeded { get; init; }

                /// <summary>Human-readable output for the LLM / user</summary>
    public string Output { get; init; } = string.Empty;

                 /// <summary>Error message (empty when Succeeded)</summary>
    public string Error { get; init; } = string.Empty;

                  /// <summary>Additional metadata (null if not applicable)</summary>
    public Dictionary<string, string>? Metadata { get; init; }

       public EToolResult() { }

            /// <summary>Create a successful tool result</summary>
   // Stateless factory — immutable data class
    public static EToolResult Success(string toolName, string output, Dictionary<string, string>? metadata = null)
              => new() { ToolName = toolName, Succeeded = true, Output = output, Metadata = metadata };

             /// <summary>Create a failed tool result with error message</summary>
   // Stateless factory — immutable data class
    public static EToolResult Failure(string toolName, string error, Dictionary<string, string>? metadata = null)
              => new() { ToolName = toolName, Succeeded = false, Error = error, Metadata = metadata };

           /// <summary>String representation for LLM context</summary>
    public override string ToString()
                 => Succeeded 
                   ? $"[SUCCESS] {ToolName}: {Output}" 
                   : $"[FAILED] {ToolName}: {Error}";
}
