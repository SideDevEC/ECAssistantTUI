using System.Text;
using ECAssistant.Config;
using ECAssistant.Services;

namespace ECAssistant.Engine;

/// <summary>
/// Manages the LLM conversation context window.
/// - Tracks typed messages with real token counts (from TokenCounter)
/// - Enforces a context-size budget (from config)
/// - Applies sliding window policy when over budget
/// - Calls SummaryService to compress old messages if needed
/// </summary>
public class ContextWindow
{
    private readonly List<TranscriptMessage> _messages = new();
    private readonly uint _maxTokens;
    private SummaryService? _summaryService;
    private uint _autoSummarizeThreshold;

      // ─── Factories ──────────────────────────────

       /// <summary>Create context window with a fixed token budget.</summary>
     public ContextWindow(uint maxTokens)
         {
              _maxTokens = maxTokens;
           _autoSummarizeThreshold = (uint)(maxTokens * 0.50f); // Summarize at 50% (v9.3: more aggressive)
            }

      /// <summary>Create context window with a fixed token budget and summary service.</summary>
    public ContextWindow(uint maxTokens, SummaryService? summaryService)
          {
              _maxTokens = maxTokens;
               _autoSummarizeThreshold = (uint)(maxTokens * 0.50f); // Summarize at 50% (v9.3: more aggressive)
                _summaryService = summaryService;
                 }

      /// <summary>Add a user message. Returns the tokens consumed.</summary>
    public int AddUserMessage(string content)
         {
            var tokens = TokenCounter.Count(content);
             _messages.Add(TranscriptMessage.User(content));
           return tokens;
            }

       /// <summary>Add an assistant response. Returns the tokens consumed.</summary>
     public int AddAssistantMessage(string content)
          {
             var tokens = TokenCounter.Count(content);
              _messages.Add(TranscriptMessage.Assistant(content));
             return tokens;
           }

       /// <summary>Add a tool output. Returns the tokens consumed.</summary>
      public int AddToolOutput(string content, string toolName = "")
        {
         var tokens = TokenCounter.Count(content);
          _messages.Add(TranscriptMessage.ToolOutput(content, toolName));
           return tokens;
            }

      /// <summary>Add system-level messages (e.g., memory injection).</summary>
     public int AddSystemMessage(string content)
        {
         var tokens = TokenCounter.Count(content);
          if (_messages.Count == 0 || _messages[0].Role != "system")
             {
                 _messages.Insert(0, TranscriptMessage.System(content));
             }
            else
                {
                  _messages[0].Content = content;
                    }
         return tokens;
          }

       /// <summary>Get all messages within the current budget. Returns as list.</summary>
    public List<TranscriptMessage> GetWindowMessages()
         {
             // Check total tokens and trigger auto-summarize if approaching or exceeding budget
           var totalEst = 0;
            foreach (var m in _messages) totalEst += m.EstimatedTokens;

           if (totalEst > (int)_maxTokens)
              {
                 SummarizeOldest(totalEst);
                  return _messages.ToList();
               }
           else if (totalEst > (int)_autoSummarizeThreshold && _messages.Count > 10)
                {
                  // Approaching budget — proactively summarize oldest 30%
                   SummarizeOldest(totalEst);
                     return _messages.ToList();
                    }

              return _messages.ToList();
         }

       /// <summary>Set the summary service for auto-summarization.</summary>
    public void SetSummaryService(SummaryService service)
           => _summaryService = service;

      /// <summary>Clear all messages and reset.</summary>
     public void Clear() { _messages.Clear(); }

       /// <summary>Check if the context is within budget. Returns true if safe.</summary>
    public bool IsWithinBudget()
         {
             var total = 0;
             foreach (var m in _messages) total += m.EstimatedTokens;
           return total <= _maxTokens;
            }

      /// <summary>The total estimated tokens currently used by all messages.</summary>
     public int GetTotalTokens()
          {
              var total = 0;
             foreach (var m in _messages) total += m.EstimatedTokens;
                return total;
                 }

       /// <summary>The number of messages in the window.</summary>
    public int MessageCount => _messages.Count;

      /// <summary>The maximum token budget for this context window.</summary>
    public uint MaxTokens => _maxTokens;

       // ─── Private: Summarize Oldest Messages ────────────────────────

     /// <summary>If total estimated tokens exceed budget, remove oldest messages and optionally summarize them.</summary>
    private async void SummarizeOldest(int currentTotal)
         {
             if (currentTotal <= _maxTokens) return;

              // Remove oldest 40% of messages to stay under budget
           var keepCount = Math.Max(5, (int)(_messages.Count * 0.3f));
            var oldMessages = new List<TranscriptMessage>();

              while (_messages.Count > keepCount && currentTotal > _maxTokens)
                  {
                    var removed = _messages[0];
                   oldMessages.Add(removed);
                       currentTotal -= removed.EstimatedTokens;
                        _messages.RemoveAt(0);
                     }

                // If we have a summary service, compress the removed messages
               if (_summaryService != null && oldMessages.Count > 3)
                   {
                       try
                           {
                                var summary = await _summaryService.SummarizeAsync(oldMessages);
                                 _messages.Insert(0, TranscriptMessage.System(summary));
                            }
                          catch (Exception ex)
                               {
                                  // Summarization failed — messages already removed, nothing to do
                              }
                         }
                      }

      /// <summary>Build a text block from messages (for summary prompts).</summary>
    private static string BuildBlockString(List<TranscriptMessage> msgs)
         {
              var sb = new StringBuilder();
           foreach (var msg in msgs)
                {
                 sb.Append($"[{msg.Role}] ");
                     sb.AppendLine(msg.Content);
                   }
               return sb.ToString();
                 }
}
