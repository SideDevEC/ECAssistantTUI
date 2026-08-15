using System.Text;
using ECAssistant.Engine;

namespace ECAssistant.Services;

/// <summary>
/// Handles LLM-based summarization of old conversation context.
/// Called by ContextWindow when the sliding window exceeds budget.
/// 
/// v2: Takes a Func&lt;string, Task&lt;string&gt;&gt; so the caller provides the prompt
/// and gets back a summary. Falls back to extractive summary if no LLM available.
/// </summary>
public class SummaryService
{
    private readonly Func<string, Task<string>>? _generateAsync;

    /// <summary>
    /// Create with an async function that takes a prompt string and returns the LLM's response.
    /// Pass null to use extractive (non-LLM) fallback summarization.
    /// </summary>
    public SummaryService(Func<string, Task<string>>? generateAsync = null)
    {
        _generateAsync = generateAsync;
    }

    /// <summary>Summarize a list of messages into a concise summary using the LLM.</summary>
    public async Task<string> SummarizeAsync(List<TranscriptMessage> messages)
    {
        if (messages == null || messages.Count == 0)
            return "(No messages to summarize.)";

        // If no LLM engine is configured, fall back to extractive summary
        if (_generateAsync == null)
        {
            var extracted = BuildExtractiveSummary(messages);
            return $"[Summary of {messages.Count} messages (extractive)]\n{extracted}";
        }

        // Build the summarization prompt from the messages
        var oldContent = BuildBlockString(messages);
        var prompt = $"Summarize the conversation below. STRICT RULES:\n- Output ONLY a concise summary of what was discussed\n- Keep facts, decisions, and tool results only\n- Do NOT add opinions, suggestions, or extra context\n- Do NOT add greetings, conclusions, or meta-commentary\n- Maximum 3 sentences\n- Plain text only, no formatting\n\nConversation:\n{oldContent}\n\nSummary:";

        try
        {
            var summary = await _generateAsync.Invoke(prompt);
            if (string.IsNullOrWhiteSpace(summary))
                return $"[Summary of {messages.Count} messages (extractive — LLM returned empty)]\n{BuildExtractiveSummary(messages)}";
            // v10.7.4: Escape angle brackets to prevent fake XML tags in context
            summary = summary.Trim().Replace("<", "&lt;").Replace(">", "&gt;");
            return $"[Summary of {messages.Count} messages]\n{summary}";
        }
        catch (Exception ex)
        {
            return $"[Summary of {messages.Count} messages (extractive — LLM error)]\n{BuildExtractiveSummary(messages)}\nError: {ex.Message}";
        }
    }

    /// <summary>Build an extractive (non-LLM) summary from message content.</summary>
    private string BuildExtractiveSummary(List<TranscriptMessage> messages)
    {
        var sb = new StringBuilder();
        // Take first 2 and last 1 for context
        var toTake = Math.Min(3, messages.Count);
        foreach (var msg in messages.Take(toTake))
        {
            if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.Append($"[{msg.Role}");
                if (!string.IsNullOrEmpty(msg.Source))
                    sb.Append($":{msg.Source}");
                sb.Append("] ");
                sb.AppendLine(msg.Content.Length > 200
                    ? msg.Content.Substring(0, 200) + "..."
                    : msg.Content);
            }
        }
        return sb.ToString();
    }

    /// <summary>Build a formatted block from messages for summary prompts.</summary>
    private string BuildBlockString(List<TranscriptMessage> msgs)
    {
        var sb = new StringBuilder();
        foreach (var msg in msgs)
        {
            sb.Append($"[{msg.Role}");
            if (!string.IsNullOrEmpty(msg.Source))
                sb.Append($":{msg.Source}");
            sb.Append("] ");
            // Truncate very long messages to avoid prompt bloat
            var content = msg.Content.Length > 500
                ? msg.Content.Substring(0, 500) + "..."
                : msg.Content;
            sb.AppendLine(content);
        }
        return sb.ToString();
    }
}