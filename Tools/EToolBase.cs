using System.Text.Json;
using ECAssistant;

namespace ECAssistant.Tools;

/// <summary>
/// Base class for all Tools. 
/// Each tool must extend this and implement ExecuteAsync().
/// New tools are added by creating a subclass — no changes to Harness required.
/// </summary>
public abstract class EToolBase
{
             /// <summary>Unique name of this tool (e.g., "EPowerShellAgent")</summary>
    public abstract string Name { get; }

          /// <summary>Human-readable description for system prompt injection</summary>
    public abstract string Description { get; }

               /// <summary>Example usage text shown to the LLM in the system prompt</summary>
    public abstract string UsageExample { get; }

                /// <summary>
                /// Execute the tool with given arguments.
                 /// </summary>
             /// <param name="arguments">Dictionary of argument key/value pairs from the LLM</param>
    public abstract Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments);

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

       private EToolResult() { }

            /// <summary>Create a successful tool result</summary>
    public static EToolResult Success(string toolName, string output, Dictionary<string, string>? metadata = null)
              => new() { ToolName = toolName, Succeeded = true, Output = output, Metadata = metadata };

             /// <summary>Create a failed tool result with error message</summary>
    public static EToolResult Failure(string toolName, string error, Dictionary<string, string>? metadata = null)
              => new() { ToolName = toolName, Succeeded = false, Error = error, Metadata = metadata };

           /// <summary>String representation for LLM context</summary>
    public override string ToString()
                 => Succeeded 
                   ? $"[SUCCESS] {ToolName}: {Output}" 
                   : $"[FAILED] {ToolName}: {Error}";
}
