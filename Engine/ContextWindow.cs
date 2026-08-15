using System.Text;
using ECAssistant.Config;
using ECAssistant.Services;

namespace ECAssistant.Engine;

/// <summary>
/// Manages the LLM conversation context window.
/// </summary>
public class ContextWindow
{
    private readonly List<TranscriptMessage> _messages = new();
    private readonly TokenCounter _tokenCounter;
    private readonly uint _maxTokens;
    private SummaryService? _summaryService;
    private uint _autoSummarizeThreshold;

    public ContextWindow(uint maxTokens, TokenCounter? tokenCounter = null)
    {
        _maxTokens = maxTokens;
        _autoSummarizeThreshold = (uint)(maxTokens * 0.50f);
        _tokenCounter = tokenCounter ?? new TokenCounter();
    }

    public ContextWindow(uint maxTokens, SummaryService? summaryService, TokenCounter? tokenCounter = null)
    {
        _maxTokens = maxTokens;
        _autoSummarizeThreshold = (uint)(maxTokens * 0.50f);
        _summaryService = summaryService;
        _tokenCounter = tokenCounter ?? new TokenCounter();
    }

    public int AddUserMessage(string content)
    {
        var tokens = _tokenCounter.Count(content);
        var msg = TranscriptMessage.User(content);
        msg.EstimatedTokens = tokens;
        _messages.Add(msg);
        return tokens;
    }

    public int AddAssistantMessage(string content)
    {
        var tokens = _tokenCounter.Count(content);
        var msg = TranscriptMessage.Assistant(content);
        msg.EstimatedTokens = tokens;
        _messages.Add(msg);
        return tokens;
    }

    public int AddToolOutput(string content, string toolName = "")
    {
        var tokens = _tokenCounter.Count(content);
        var msg = TranscriptMessage.ToolOutput(content, toolName);
        msg.EstimatedTokens = tokens;
        _messages.Add(msg);
        return tokens;
    }

    public int AddSystemMessage(string content)
    {
        var tokens = _tokenCounter.Count(content);
        if (_messages.Count == 0 || _messages[0].Role != "system")
        {
            var msg = TranscriptMessage.System(content);
            msg.EstimatedTokens = tokens;
            _messages.Insert(0, msg);
        }
        else
        {
            _messages[0].Content = content;
            _messages[0].EstimatedTokens = tokens;
        }
        return tokens;
    }

    public List<TranscriptMessage> GetWindowMessages()
    {
        var totalEst = 0;
        foreach (var m in _messages) totalEst += m.EstimatedTokens;

        if (totalEst > (int)_maxTokens)
        {
            SummarizeOldest(totalEst);
            return _messages.ToList();
        }
        else if (totalEst > (int)_autoSummarizeThreshold && _messages.Count > 10)
        {
            SummarizeOldest(totalEst);
            return _messages.ToList();
        }

        return _messages.ToList();
    }

    public void SetSummaryService(SummaryService service) => _summaryService = service;

    public bool RemoveLastAssistantMessage()
    {
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            if (_messages[i].Role == "assistant")
            {
                _messages.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    public void Clear() => _messages.Clear();

    public bool IsWithinBudget()
    {
        var total = 0;
        foreach (var m in _messages) total += m.EstimatedTokens;
        return total <= _maxTokens;
    }

    public int GetTotalTokens()
    {
        var total = 0;
        foreach (var m in _messages) total += m.EstimatedTokens;
        return total;
    }

    public int MessageCount => _messages.Count;
    public uint MaxTokens => _maxTokens;

    private async void SummarizeOldest(int currentTotal)
    {
        if (currentTotal <= _maxTokens) return;

        var keepCount = Math.Max(5, (int)(_messages.Count * 0.3f));
        var oldMessages = new List<TranscriptMessage>();

        while (_messages.Count > keepCount && currentTotal > _maxTokens)
        {
            var removed = _messages[0];
            oldMessages.Add(removed);
            currentTotal -= removed.EstimatedTokens;
            _messages.RemoveAt(0);
        }

        if (_summaryService != null && oldMessages.Count > 3)
        {
            try
            {
                var summary = await _summaryService.SummarizeAsync(oldMessages);
                _messages.Insert(0, TranscriptMessage.System(summary));
            }
            catch { }
        }
    }

    private string BuildBlockString(List<TranscriptMessage> msgs)
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