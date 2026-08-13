using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ECAssistant.Services;

/// <summary>
/// Background Process Manager — starts, tracks, and manages long-running processes
/// without blocking the main thread.
/// 
/// Usage:
///   var id = await _bgMgr.StartAsync("dotnet build", workingDir);
///   var status = _bgMgr.GetStatus(id);  // Running, Completed, Failed
///   var output = _bgMgr.GetOutput(id);  // stdout/stderr so far
///   _bgMgr.Kill(id);                    // terminate
///   _bgMgr.List();                       // all active processes
/// </summary>
public class BackgroundProcessManager : IDisposable
{
    private readonly ConcurrentDictionary<string, BgProcess> _processes = new();
    private int _counter = 0;

    /// <summary>Start a background process (non-blocking).</summary>
    public async Task<string> StartAsync(string command, string workingDirectory, int timeoutSeconds = 300)
    {
        var id = $"bg-{++_counter}";
        // v10.19.2: Temp scripts inside working dir, not OS temp. OS-aware.
        var tempDir = Path.Combine(workingDirectory, ".tmp");
        Directory.CreateDirectory(tempDir);
        var isWindows = OperatingSystem.IsWindows();
        var ext = isWindows ? ".ps1" : ".sh";
        var tempScript = Path.Combine(tempDir, $"ecagent_bg_{id}{ext}");
        await File.WriteAllTextAsync(tempScript, command);

        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "powershell.exe" : "/bin/zsh",
            Arguments = isWindows
                ? $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\""
                : $"-c \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start background process.");

        var bgProc = new BgProcess
        {
            Id = id,
            Process = proc,
            Command = command,
            StartedAt = DateTime.UtcNow,
            TimeoutSeconds = timeoutSeconds,
            TempScriptPath = tempScript,
        };

        // Capture output asynchronously
        bgProc.OutputTask = Task.Run(() => proc.StandardOutput.ReadToEndAsync());
        bgProc.ErrorTask = Task.Run(() => proc.StandardError.ReadToEndAsync());

        // Set up completion + timeout
        _ = Task.Run(async () =>
        {
            try
            {
                var completed = await Task.Run(() => proc.WaitForExit(timeoutSeconds * 1000));
                if (!completed && !proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    bgProc.TimedOut = true;
                }
            }
            catch { }
            finally
            {
                bgProc.CompletedAt = DateTime.UtcNow;
                bgProc.ExitCode = proc.HasExited ? proc.ExitCode : -1;
                bgProc.IsFinished = true;
                try { File.Delete(tempScript); } catch { }
            }
        });

        _processes[id] = bgProc;
        return id;
    }

    /// <summary>Get the status of a background process.</summary>
    public BgStatus GetStatus(string id)
    {
        if (!_processes.TryGetValue(id, out var bg))
            return BgStatus.NotFound;

        if (bg.IsFinished)
            return bg.ExitCode == 0 ? BgStatus.Completed : (bg.TimedOut ? BgStatus.TimedOut : BgStatus.Failed);

        return BgStatus.Running;
    }

    /// <summary>Get output (stdout + stderr) from a background process.</summary>
    public string GetOutput(string id)
    {
        if (!_processes.TryGetValue(id, out var bg))
            return $"Process '{id}' not found.";

        var stdout = bg.OutputTask?.IsCompleted == true ? bg.OutputTask.Result : "";
        var stderr = bg.ErrorTask?.IsCompleted == true ? bg.ErrorTask.Result : "";

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(stdout)) sb.AppendLine(stdout);
        if (!string.IsNullOrEmpty(stderr)) sb.AppendLine($"[STDERR] {stderr}");
        return sb.ToString();
    }

    /// <summary>Get full status info for a background process.</summary>
    public BgProcessInfo GetInfo(string id)
    {
        if (!_processes.TryGetValue(id, out var bg))
            return new BgProcessInfo { Id = id, Status = BgStatus.NotFound };

        var elapsed = bg.CompletedAt.HasValue
            ? (bg.CompletedAt.Value - bg.StartedAt).TotalSeconds
            : (DateTime.UtcNow - bg.StartedAt).TotalSeconds;

        return new BgProcessInfo
        {
            Id = id,
            Command = bg.Command,
            Status = GetStatus(id),
            ExitCode = bg.ExitCode,
            ElapsedSeconds = elapsed,
            StartedAt = bg.StartedAt,
            CompletedAt = bg.CompletedAt
        };
    }

    /// <summary>Kill a running background process.</summary>
    public bool Kill(string id)
    {
        if (!_processes.TryGetValue(id, out var bg)) return false;
        if (bg.IsFinished) return false;

        try
        {
            bg.Process.Kill(entireProcessTree: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>List all background processes.</summary>
    public List<BgProcessInfo> List()
    {
        var result = new List<BgProcessInfo>();
        foreach (var kvp in _processes)
        {
            result.Add(GetInfo(kvp.Key));
        }
        return result;
    }

    /// <summary>Clean up finished processes (remove from tracking).</summary>
    public void CleanupFinished()
    {
        var toRemove = _processes
            .Where(kvp => kvp.Value.IsFinished)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
            _processes.TryRemove(key, out _);
    }

    public void Dispose()
    {
        foreach (var bg in _processes.Values)
        {
            try { if (!bg.Process.HasExited) bg.Process.Kill(entireProcessTree: true); } catch { }
        }
        _processes.Clear();
    }
}

/// <summary>Internal tracked background process.</summary>
internal class BgProcess
{
    public string Id { get; set; } = "";
    public Process Process { get; set; } = null!;
    public string Command { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int TimeoutSeconds { get; set; }
    public int ExitCode { get; set; } = -1;
    public bool IsFinished { get; set; }
    public bool TimedOut { get; set; }
    public string TempScriptPath { get; set; } = "";
    public Task<string>? OutputTask { get; set; }
    public Task<string>? ErrorTask { get; set; }
}

/// <summary>Status of a background process.</summary>
public enum BgStatus
{
    Running,
    Completed,
    Failed,
    TimedOut,
    NotFound
}

/// <summary>Public info about a background process.</summary>
public class BgProcessInfo
{
    public string Id { get; set; } = "";
    public string Command { get; set; } = "";
    public BgStatus Status { get; set; }
    public int ExitCode { get; set; }
    public double ElapsedSeconds { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public override string ToString()
    {
        var elapsed = ElapsedSeconds > 60
            ? $"{(int)ElapsedSeconds / 60}m {(int)ElapsedSeconds % 60}s"
            : $"{ElapsedSeconds:F1}s";
        return $"[{Id}] {Status} | {elapsed} | exit={ExitCode} | {Command.Substring(0, Math.Min(Command.Length, 60))}";
    }
}