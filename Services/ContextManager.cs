using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// Context window management with summary-and-shift strategy.
/// </summary>
public class ContextManager : IContextManager
{
    private readonly IInferenceEngine _inferenceEngine;
    private readonly IConfigProvider _configProvider;
    private readonly List<TranscriptMessage> _messages;
    private readonly int _shiftAtMessages;
    private readonly int _keepLast;

    public int MessageCount => _messages.Count;

    public ContextManager(IInferenceEngine inferenceEngine, IConfigProvider configProvider)
    {
        _inferenceEngine = inferenceEngine ?? throw new ArgumentNullException(nameof(inferenceEngine));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _messages = new List<TranscriptMessage>();
        _shiftAtMessages = configProvider.GetInt("context_management.shift_at_messages", 40);
        _keepLast = configProvider.GetInt("context_management.keep_last", 20);
    }

    public void AddMessage(TranscriptMessage message)
    {
        _messages.Add(message);
    }

    public List<TranscriptMessage> GetMessages()
    {
        return new List<TranscriptMessage>(_messages);
    }

    public async Task<string> SummarizeAsync()
    {
        var prompt = "Summarize the key decisions, actions taken, and current state of our conversation. Keep it concise but include any important findings or tool results that will help me continue this task.";

        var conversation = string.Join("\n", _messages.Select(m => $"[{m.Role}]: {m.Content}"));
        var fullPrompt = $"{prompt}\n\nConversation:\n{conversation}";

        return await _inferenceEngine.GenerateAsync(fullPrompt);
    }

    public bool NeedsShift()
    {
        return _messages.Count >= _shiftAtMessages;
    }

    public void Shift()
    {
        if (_messages.Count <= _keepLast) return;

        var keep = _messages.TakeLast(_keepLast).ToList();
        _messages.Clear();
        _messages.AddRange(keep);
    }
}