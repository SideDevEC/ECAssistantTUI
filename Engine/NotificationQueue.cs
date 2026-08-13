using System.Collections.Concurrent;
using ECAssistant.UI;

namespace ECAssistant.Engine;

/// <summary>
/// Notification priority level.
/// </summary>
public enum NotificationPriority
{
    /// <summary>Informational — shown but doesn't interrupt.</summary>
    Info,
    /// <summary>Important — shown with emphasis.</summary>
    Warning,
    /// <summary>Critical — shown immediately, may require user action.</summary>
    Critical
}

/// <summary>
/// A single notification from a background sub-agent to the main agent / UI.
/// </summary>
public class AgentNotification
{
    /// <summary>Unique notification ID.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Which background agent sent this.</summary>
    public string AgentId { get; set; } = "";

    /// <summary>Human-readable agent name.</summary>
    public string AgentName { get; set; } = "";

    /// <summary>Notification message.</summary>
    public string Message { get; set; } = "";

    /// <summary>Priority level.</summary>
    public NotificationPriority Priority { get; set; } = NotificationPriority.Info;

    /// <summary>When the notification was created.</summary>
    public DateTime Timestamp { get; } = DateTime.UtcNow;

    /// <summary>Optional structured data (for programmatic use).</summary>
    public Dictionary<string, string> Data { get; set; } = new();

    /// <summary>Whether the main agent should be interrupted to handle this.</summary>
    public bool RequiresAttention { get; set; } = false;

    /// <summary>Format for console display.</summary>
    public string ToDisplayString()
    {
        var icon = Priority switch
        {
            NotificationPriority.Critical => "🚨",
            NotificationPriority.Warning => "⚠️",
            _ => "📢"
        };
        var time = Timestamp.ToString("HH:mm:ss");
        return $"{icon} [{time}] [{AgentName}] {Message}";
    }
}

/// <summary>
/// Thread-safe notification queue — the communication channel between
/// background sub-agents and the main agent / UI.
///
/// Background agents push notifications here.
/// The main orchestrator drains them between turns and displays to the user.
/// </summary>
public sealed class NotificationQueue : IDisposable
{
    private readonly ConcurrentQueue<AgentNotification> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private static readonly object _consoleLock = new(); // Fix #7: Thread-safe console writes
    private bool _disposed = false;

    // Fix #10: Removed unused _lock field — ConcurrentQueue and SemaphoreSlim are already thread-safe

    private int _totalQueued;
    private int _totalDrained;

    /// <summary>Total notifications ever queued.</summary>
    public int TotalQueued => _totalQueued;

    /// <summary>Total notifications ever drained.</summary>
    public int TotalDrained => _totalDrained;

    /// <summary>Push a notification from a background agent.</summary>
    public void Push(AgentNotification notification)
    {
        if (_disposed) return;

        _queue.Enqueue(notification);
        Interlocked.Increment(ref _totalQueued);
        _signal.Release();

        // Fix #7: Use lock to prevent interleaved console output from concurrent notifications
        try
        {
            var color = notification.Priority switch
            {
                NotificationPriority.Critical => EColor.Error(),
                NotificationPriority.Warning => EColor.Warn(),
                _ => EColor.Info()
            };
            // Console.Out is not thread-safe for multi-line writes — use lock
            lock (_consoleLock)
            {
                EColor.TagBold(color, "Notify", notification.ToDisplayString());
            }
        }
        catch { }
    }

    /// <summary>Convenience method to push a simple notification.</summary>
    public void Push(string agentId, string agentName, string message,
        NotificationPriority priority = NotificationPriority.Info,
        bool requiresAttention = false)
    {
        Push(new AgentNotification
        {
            AgentId = agentId,
            AgentName = agentName,
            Message = message,
            Priority = priority,
            RequiresAttention = requiresAttention,
        });
    }

    /// <summary>
    /// Drain all pending notifications (non-blocking).
    /// Returns empty list if none pending.
    /// </summary>
    public List<AgentNotification> DrainAll()
    {
        var result = new List<AgentNotification>();
        while (_queue.TryDequeue(out var notification))
        {
            result.Add(notification);
            Interlocked.Increment(ref _totalDrained);
        }
        return result;
    }

    /// <summary>
    /// Wait for at least one notification (blocking, with timeout).
    /// Returns all available notifications.
    /// </summary>
    public async Task<List<AgentNotification>> WaitForNotificationsAsync(int timeoutMs = 1000)
    {
        try
        {
            await _signal.WaitAsync(timeoutMs);
        }
        catch (OperationCanceledException) { }

        return DrainAll();
    }

    /// <summary>Check if there are pending notifications without draining.</summary>
    public bool HasPending => !_queue.IsEmpty;

    /// <summary>Get count of pending notifications.</summary>
    public int PendingCount => _queue.Count;

    public void Dispose()
    {
        _disposed = true;
        _signal.Dispose();
    }
}