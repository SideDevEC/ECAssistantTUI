using ECAssistant.Services;

namespace ECAssistant.Engine;

/// <summary>
/// Task Planner — breaks complex requests into sub-tasks, tracks progress, and adapts.
/// 
/// The LLM doesn't explicitly decompose tasks — instead, this manager:
/// 1. Detects when a request is complex (keywords: "and", "then", "after that", "also")
/// 2. Splits into sub-tasks based on those keywords
/// 3. Tracks completion of each sub-task
/// 4. Injects progress context into each prompt
/// 5. Adapts if a sub-task fails (marks failed, continues with remaining)
/// </summary>
public class TaskPlanner
{
    private readonly List<SubTask> _subTasks = new();
    private int _currentSubTask = 0;

    /// <summary>Analyze a user request and decompose into sub-tasks if complex.</summary>
    public List<SubTask> Decompose(string request)
    {
        _subTasks.Clear();
        _currentSubTask = 0;

        // Detect complexity indicators
        var hasMultipleSteps = ContainsAny(request, new[] { " and then ", " then ", " after that ", " also ", " and ", " finally ", " next " });
        var hasMultipleActions = CountActions(request) >= 2;

        if (!hasMultipleSteps && !hasMultipleActions)
        {
            // Simple task — single sub-task
            _subTasks.Add(new SubTask { Description = request, Status = SubTaskStatus.Pending });
            return _subTasks;
        }

        // Split on step indicators
        var parts = SplitOnSteps(request);
        foreach (var part in parts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            _subTasks.Add(new SubTask
            {
                Description = part.Trim(),
                Status = SubTaskStatus.Pending
            });
        }

        if (_subTasks.Count == 0)
            _subTasks.Add(new SubTask { Description = request, Status = SubTaskStatus.Pending });

        Logger.Info("TaskPlanner", $"Decomposed into {_subTasks.Count} sub-task(s)");
        return _subTasks;
    }

    /// <summary>Get the current sub-task.</summary>
    public SubTask? Current => _currentSubTask < _subTasks.Count ? _subTasks[_currentSubTask] : null;

    /// <summary>Mark current sub-task as completed and advance.</summary>
    public void CompleteCurrent()
    {
        if (_currentSubTask < _subTasks.Count)
        {
            _subTasks[_currentSubTask].Status = SubTaskStatus.Completed;
            _subTasks[_currentSubTask].CompletedAt = DateTime.UtcNow;
            _currentSubTask++;
            Logger.Info("TaskPlanner", $"Sub-task {_currentSubTask} completed");
        }
    }

    /// <summary>Mark current sub-task as failed and advance.</summary>
    public void FailCurrent(string reason)
    {
        if (_currentSubTask < _subTasks.Count)
        {
            _subTasks[_currentSubTask].Status = SubTaskStatus.Failed;
            _subTasks[_currentSubTask].FailureReason = reason;
            _currentSubTask++;
            Logger.Warn("TaskPlanner", $"Sub-task {_currentSubTask} failed: {reason}");
        }
    }

    /// <summary>Get progress context for prompt injection.</summary>
    public string GetProgressContext()
    {
        if (_subTasks.Count <= 1) return ""; // simple task, no context needed

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[TASK PROGRESS] Step {_currentSubTask + 1}/{_subTasks.Count}:");
        for (int i = 0; i < _subTasks.Count; i++)
        {
            var status = _subTasks[i].Status switch
            {
                SubTaskStatus.Completed => "✅",
                SubTaskStatus.Failed => "❌",
                SubTaskStatus.InProgress => "🔄",
                _ => "⬜"
            };
            var marker = i == _currentSubTask ? " → " : "   ";
            sb.AppendLine($"{marker}{status} {_subTasks[i].Description}");
        }
        return sb.ToString();
    }

    /// <summary>Are there remaining sub-tasks?</summary>
    public bool HasRemaining => _currentSubTask < _subTasks.Count;

    /// <summary>Get summary of all sub-tasks (for final output).</summary>
    public string GetSummary()
    {
        var completed = _subTasks.Count(s => s.Status == SubTaskStatus.Completed);
        var failed = _subTasks.Count(s => s.Status == SubTaskStatus.Failed);
        return $"{completed}/{_subTasks.Count} completed, {failed} failed";
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static bool ContainsAny(string text, string[] patterns)
    {
        foreach (var p in patterns)
            if (text.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int CountActions(string request)
    {
        var actionWords = new[] { "build", "create", "add", "remove", "update", "fix", "replace", "refactor", "test", "delete", "move", "copy" };
        var count = 0;
        foreach (var word in actionWords)
            if (request.Contains(word, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    private static List<string> SplitOnSteps(string request)
    {
        var separators = new[] { " and then ", " then ", " after that ", " also ", " finally ", " next " };
        var result = new List<string> { request };

        foreach (var sep in separators)
        {
            var newResult = new List<string>();
            foreach (var part in result)
            {
                var idx = part.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    newResult.Add(part.Substring(0, idx));
                    newResult.Add(part.Substring(idx + sep.Length));
                }
                else
                {
                    newResult.Add(part);
                }
            }
            result = newResult;
        }

        // Don't split on plain " and " if it would create tiny fragments
        if (result.Count <= 1)
        {
            var idx = request.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && request.Length > 30)
            {
                result = new List<string> {
                    request.Substring(0, idx),
                    request.Substring(idx + 5)
                };
            }
        }

        return result;
    }
}

// ─── Data Structures ──────────────────────────────────────────────

public class SubTask
{
    public string Description { get; set; } = "";
    public SubTaskStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
}

public enum SubTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}