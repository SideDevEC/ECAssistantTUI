using System.Collections.Generic;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// Context window management with summary-and-shift strategy.
/// </summary>
public interface IContextManager
{
    void AddMessage(TranscriptMessage message);
    List<TranscriptMessage> GetMessages();
    Task<string> SummarizeAsync();
    bool NeedsShift();
    void Shift();
    int MessageCount { get; }
}