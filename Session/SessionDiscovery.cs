using System.Text.Json;

namespace ECAssistant.Session;

/// <summary>
/// Discovers existing sessions on disk and determines which one to load as active.
/// Also handles migration of legacy transcript.json to the .sessions/&lt;key&gt;/ layout.
/// </summary>
public static class SessionDiscovery
{
    /// <summary>Session metadata file (stored in each session directory).</summary>
    private const string MetaFileName = "session_meta.json";

    /// <summary>
    /// Scan the .sessions/ directory for existing sessions.
    /// Returns a list of session keys found, ordered by last-modified time (newest first).
    /// </summary>
    public static List<string> DiscoverSessions(string workingDir)
    {
        var sessionsDir = Path.Combine(workingDir, ".sessions");
        if (!Directory.Exists(sessionsDir))
            return new List<string>();

        var result = new List<(string Key, DateTime LastModified)>();

        foreach (var dir in Directory.GetDirectories(sessionsDir))
        {
            var key = Path.GetFileName(dir);
            var metaPath = Path.Combine(dir, MetaFileName);
            DateTime lastMod;

            if (File.Exists(metaPath))
            {
                // Use meta file's last write time
                lastMod = File.GetLastWriteTimeUtc(metaPath);
            }
            else
            {
                // Fall back to transcript or any file in the dir
                var transPath = Path.Combine(dir, "transcript.json");
                if (File.Exists(transPath))
                    lastMod = File.GetLastWriteTimeUtc(transPath);
                else
                    lastMod = Directory.GetLastWriteTimeUtc(dir);
            }

            result.Add((key, lastMod));
        }

        return result.OrderByDescending(x => x.LastModified).Select(x => x.Key).ToList();
    }

    /// <summary>
    /// Find the most recently modified session. Returns null if no sessions exist.
    /// </summary>
    public static string? FindLastActiveSession(string workingDir)
    {
        var sessions = DiscoverSessions(workingDir);
        return sessions.Count > 0 ? sessions[0] : null;
    }

    /// <summary>
    /// Migrate legacy transcript.json from the working dir root to .sessions/main/.
    /// Only migrates if .sessions/main/transcript.json doesn't exist.
    /// </summary>
    public static void MigrateLegacyTranscript(string workingDir)
    {
        var legacyPath = Path.Combine(workingDir, "transcript.json");
        var sessionsDir = Path.Combine(workingDir, ".sessions");
        var mainSessionDir = Path.Combine(sessionsDir, "main");

        if (!File.Exists(legacyPath)) return;
        if (!Directory.Exists(mainSessionDir)) return;

        var targetPath = Path.Combine(mainSessionDir, "transcript.json");
        if (File.Exists(targetPath)) return; // already exists, don't overwrite

        try
        {
            File.Copy(legacyPath, targetPath);
            // Keep the legacy file for now — don't delete, let user clean up
        }
        catch { }
    }

    /// <summary>
    /// Ensure .sessions/ directory exists.
    /// </summary>
    public static void EnsureSessionsDir(string workingDir)
    {
        var sessionsDir = Path.Combine(workingDir, ".sessions");
        Directory.CreateDirectory(sessionsDir);
    }

    /// <summary>
    /// Save session metadata (updates last-modified time on the meta file).
    /// Called when a session is created or switched to.
    /// </summary>
    public static void TouchSessionMeta(string workingDir, string sessionKey)
    {
        var sessionDir = Path.Combine(workingDir, ".sessions", sessionKey);
        Directory.CreateDirectory(sessionDir);

        var metaPath = Path.Combine(sessionDir, MetaFileName);
        var meta = new SessionMeta
        {
            Key = sessionKey,
            LastActive = DateTime.UtcNow
        };

        try
        {
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);
        }
        catch { }
    }

    /// <summary>Session metadata stored on disk.</summary>
    private class SessionMeta
    {
        public string Key { get; set; } = "";
        public DateTime LastActive { get; set; }
    }
}