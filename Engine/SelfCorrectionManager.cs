using System.Text.Json;
using ECAssistant.Services;

namespace ECAssistant.Engine;

/// <summary>
/// Self-Correction Manager — detects failure loops, manages file rollback snapshots,
/// and provides context-aware fix suggestions.
/// 
/// Capabilities:
/// - Track tool failures and detect repetition (same error 3x → escalate to user)
/// - Snapshot files before modification (rollback on repeated failure)
/// - Analyze error patterns and suggest fix strategies
/// - Maintain a failure history for the current task
/// </summary>
public class SelfCorrectionManager : IDisposable
{
    private readonly List<FailureEntry> _failures = new();
    private readonly List<FileSnapshot> _snapshots = new();
    private readonly string _snapshotDir;
    private int _maxHistory = 20;

    public SelfCorrectionManager(string workingDir)
    {
        _snapshotDir = Path.Combine(workingDir, ".snapshots");
        if (!Directory.Exists(_snapshotDir)) Directory.CreateDirectory(_snapshotDir);
    }

    /// <summary>Record a tool failure and check for loops.</summary>
    public FailureAnalysis RecordFailure(string toolName, string errorMessage, string? command = null)
    {
        var entry = new FailureEntry
        {
            ToolName = toolName,
            ErrorMessage = errorMessage,
            Command = command ?? "",
            Timestamp = DateTime.UtcNow
        };
        _failures.Add(entry);

        // Trim history
        if (_failures.Count > _maxHistory) _failures.RemoveAt(0);

        return AnalyseFailures();
    }

    /// <summary>Analyze failure patterns and return a recommendation.</summary>
    public FailureAnalysis AnalyseFailures()
    {
        if (_failures.Count == 0)
            return new FailureAnalysis { Pattern = FailurePattern.None, Recommendation = "" };

        // Check for same error repeated 3+ times
        var recentErrors = _failures.TakeLast(5).Select(f => f.ErrorMessage.Trim()).ToList();
        var errorGroups = recentErrors.GroupBy(e => e).OrderByDescending(g => g.Count());
        var mostCommon = errorGroups.FirstOrDefault();

        if (mostCommon != null && mostCommon.Count() >= 3)
        {
            return new FailureAnalysis
            {
                Pattern = FailurePattern.RepeatedError,
                Recommendation = $"[ESCALATE] The same error has occurred {mostCommon.Count()} times: '{mostCommon.Key.Substring(0, Math.Min(mostCommon.Key.Length, 100))}'. " +
                    "Stop retrying the same approach. Try a different strategy or ask the user for guidance.",
                ShouldEscalate = true
            };
        }

        // Check for same tool failing 3+ times
        var toolGroups = _failures.TakeLast(5).GroupBy(f => f.ToolName).OrderByDescending(g => g.Count());
        var mostFailedTool = toolGroups.FirstOrDefault();
        if (mostFailedTool != null && mostFailedTool.Count() >= 3)
        {
            return new FailureAnalysis
            {
                Pattern = FailurePattern.ToolLoop,
                Recommendation = $"[ESCALATE] Tool '{mostFailedTool.Key}' has failed {mostFailedTool.Count()} times. " +
                    "The approach isn't working. Try a different tool or ask the user for help.",
                ShouldEscalate = true
            };
        }

        // Check for alternating pattern (fix A breaks B, fix B breaks A)
        if (_failures.Count >= 4)
        {
            var last4 = _failures.TakeLast(4).ToList();
            if (last4[0].ErrorMessage == last4[2].ErrorMessage && last4[1].ErrorMessage == last4[3].ErrorMessage)
            {
                return new FailureAnalysis
                {
                    Pattern = FailurePattern.Alternating,
                    Recommendation = "[ESCALATE] Detected an alternating failure pattern (fixing one thing breaks another). " +
                        "Step back and consider a different approach that fixes both issues at once.",
                    ShouldEscalate = true
                };
            }
        }

        return new FailureAnalysis
        {
            Pattern = FailurePattern.Isolated,
            Recommendation = "",
            ShouldEscalate = false
        };
    }

    /// <summary>Snapshot a file before modification (for rollback).</summary>
    public async Task<string?> SnapshotFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var snapshotId = $"snap_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Path.GetFileName(filePath)}";
        var snapshotPath = Path.Combine(_snapshotDir, snapshotId);

        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            var snapshot = new FileSnapshot
            {
                SnapshotId = snapshotId,
                OriginalPath = filePath,
                Content = content,
                Timestamp = DateTime.UtcNow
            };
            _snapshots.Add(snapshot);
            Logger.Info("SelfCorrect", $"Snapshotted: {filePath} → {snapshotId}");
            return snapshotId;
        }
        catch (Exception ex)
        {
            Logger.Error("SelfCorrect", $"Snapshot failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Rollback a file to its last snapshot.</summary>
    public async Task<bool> RollbackAsync(string filePath)
    {
        var snapshot = _snapshots.LastOrDefault(s => s.OriginalPath == filePath);
        if (snapshot == null)
        {
            Logger.Warn("SelfCorrect", $"No snapshot found for: {filePath}");
            return false;
        }

        try
        {
            await File.WriteAllTextAsync(snapshot.OriginalPath, snapshot.Content);
            Logger.Info("SelfCorrect", $"Rolled back: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("SelfCorrect", $"Rollback failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Clear failure history (on successful task completion).</summary>
    public void ClearHistory()
    {
        _failures.Clear();
        _snapshots.Clear();
        Logger.Debug("SelfCorrect", "History cleared.");
    }

    /// <summary>Get failure count for current task.</summary>
    public int FailureCount => _failures.Count;

    /// <summary>Get recent failures summary (for LLM injection).</summary>
    public string GetFailureSummary()
    {
        if (_failures.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[FAILURE HISTORY] {_failures.Count} recent failure(s):");
        foreach (var f in _failures.TakeLast(5))
        {
            sb.AppendLine($"  {f.ToolName}: {f.ErrorMessage.Substring(0, Math.Min(f.ErrorMessage.Length, 100))}");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        // Clean up old snapshots (keep last 10)
        var oldSnapshots = _snapshots.SkipLast(10).ToList();
        foreach (var snap in oldSnapshots)
        {
            var path = Path.Combine(_snapshotDir, snap.SnapshotId);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}

// ─── Data Structures ──────────────────────────────

public class FailureEntry
{
    public string ToolName { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string Command { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

public class FileSnapshot
{
    public string SnapshotId { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

public class FailureAnalysis
{
    public FailurePattern Pattern { get; set; }
    public string Recommendation { get; set; } = "";
    public bool ShouldEscalate { get; set; }
}

public enum FailurePattern
{
    None,
    Isolated,       // single failure, normal
    RepeatedError,  // same error 3+ times
    ToolLoop,       // same tool failing 3+ times
    Alternating     // fix A breaks B pattern
}