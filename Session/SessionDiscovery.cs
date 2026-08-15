using System.Text.Json;

namespace ECAssistant.Session;

/// <summary>
/// Discovers existing sessions on disk and determines which one to load as active.
/// </summary>
public class SessionDiscovery
{
    private const string MetaFileName = "session_meta.json";

    public List<string> DiscoverSessions(string workingDir)
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
                lastMod = File.GetLastWriteTimeUtc(metaPath);
            else
            {
                var transPath = Path.Combine(dir, "transcript.json");
                lastMod = File.Exists(transPath)
                    ? File.GetLastWriteTimeUtc(transPath)
                    : Directory.GetLastWriteTimeUtc(dir);
            }

            result.Add((key, lastMod));
        }

        return result.OrderByDescending(x => x.LastModified).Select(x => x.Key).ToList();
    }

    public string? FindLastActiveSession(string workingDir)
    {
        var sessions = DiscoverSessions(workingDir);
        return sessions.Count > 0 ? sessions[0] : null;
    }

    public void MigrateLegacyTranscript(string workingDir)
    {
        var legacyPath = Path.Combine(workingDir, "transcript.json");
        var sessionsDir = Path.Combine(workingDir, ".sessions");
        var mainSessionDir = Path.Combine(sessionsDir, "main");

        if (!File.Exists(legacyPath)) return;
        if (!Directory.Exists(mainSessionDir)) return;

        var targetPath = Path.Combine(mainSessionDir, "transcript.json");
        if (File.Exists(targetPath)) return;

        try { File.Copy(legacyPath, targetPath); } catch { }
    }

    public void EnsureSessionsDir(string workingDir)
    {
        var sessionsDir = Path.Combine(workingDir, ".sessions");
        Directory.CreateDirectory(sessionsDir);
    }

    public void TouchSessionMeta(string workingDir, string sessionKey)
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

    private class SessionMeta
    {
        public string Key { get; set; } = "";
        public DateTime LastActive { get; set; }
    }
}