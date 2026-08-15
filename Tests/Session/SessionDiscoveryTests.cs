using ECAssistant.Session;

namespace ECAssistant.Tests.Session;

public class SessionDiscoveryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionDiscovery _discovery;

    public SessionDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAssistantTests_SD_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _discovery = new SessionDiscovery();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void CreateSessionDir(string sessionKey, bool withMeta = true, bool withTranscript = false)
    {
        var sessionDir = Path.Combine(_tempDir, ".sessions", sessionKey);
        Directory.CreateDirectory(sessionDir);

        if (withMeta)
        {
            var metaPath = Path.Combine(sessionDir, "session_meta.json");
            File.WriteAllText(metaPath, "{\"Key\":\"" + sessionKey + "\",\"LastActive\":\"2025-01-01T00:00:00Z\"}");
            // Touch the meta file to set a specific time
            File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow.AddMinutes(-10));
        }

        if (withTranscript)
        {
            var transPath = Path.Combine(sessionDir, "transcript.json");
            File.WriteAllText(transPath, "[]");
        }
    }

    [Fact]
    public void DiscoverSessions_NoSessionsDir_ReturnsEmptyList()
    {
        var result = _discovery.DiscoverSessions(_tempDir);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverSessions_WithSessions_ReturnsAllSessionKeys()
    {
        CreateSessionDir("session1");
        CreateSessionDir("session2");
        CreateSessionDir("session3");

        var result = _discovery.DiscoverSessions(_tempDir);
        Assert.Equal(3, result.Count);
        Assert.Contains("session1", result);
        Assert.Contains("session2", result);
        Assert.Contains("session3", result);
    }

    [Fact]
    public void DiscoverSessions_OrdersByLastModifiedDescending()
    {
        CreateSessionDir("older");
        // Make "newer" session more recent
        CreateSessionDir("newer");
        var newerMeta = Path.Combine(_tempDir, ".sessions", "newer", "session_meta.json");
        File.SetLastWriteTimeUtc(newerMeta, DateTime.UtcNow);
        var olderMeta = Path.Combine(_tempDir, ".sessions", "older", "session_meta.json");
        File.SetLastWriteTimeUtc(olderMeta, DateTime.UtcNow.AddHours(-2));

        var result = _discovery.DiscoverSessions(_tempDir);
        Assert.Equal("newer", result[0]);
        Assert.Equal("older", result[1]);
    }

    [Fact]
    public void DiscoverSessions_WithTranscriptFallback_UsesTranscriptTime()
    {
        // Session without meta but with transcript
        var sessionDir = Path.Combine(_tempDir, ".sessions", "transcriptOnly");
        Directory.CreateDirectory(sessionDir);
        var transPath = Path.Combine(sessionDir, "transcript.json");
        File.WriteAllText(transPath, "[]");
        File.SetLastWriteTimeUtc(transPath, DateTime.UtcNow.AddMinutes(-5));

        // Session with meta
        CreateSessionDir("withMeta");
        var metaPath = Path.Combine(_tempDir, ".sessions", "withMeta", "session_meta.json");
        File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow.AddHours(-1));

        var result = _discovery.DiscoverSessions(_tempDir);
        // transcriptOnly was modified more recently
        Assert.Equal("transcriptOnly", result[0]);
    }

    [Fact]
    public void DiscoverSessions_EmptySessionDir_UsesDirectoryTime()
    {
        var sessionDir = Path.Combine(_tempDir, ".sessions", "emptySession");
        Directory.CreateDirectory(sessionDir);
        Directory.SetLastWriteTimeUtc(sessionDir, DateTime.UtcNow.AddMinutes(-1));

        var result = _discovery.DiscoverSessions(_tempDir);
        Assert.Single(result);
        Assert.Equal("emptySession", result[0]);
    }

    [Fact]
    public void FindLastActiveSession_NoSessions_ReturnsNull()
    {
        var result = _discovery.FindLastActiveSession(_tempDir);
        Assert.Null(result);
    }

    [Fact]
    public void FindLastActiveSession_WithSessions_ReturnsMostRecent()
    {
        CreateSessionDir("older");
        var olderMeta = Path.Combine(_tempDir, ".sessions", "older", "session_meta.json");
        File.SetLastWriteTimeUtc(olderMeta, DateTime.UtcNow.AddHours(-2));

        CreateSessionDir("newer");
        var newerMeta = Path.Combine(_tempDir, ".sessions", "newer", "session_meta.json");
        File.SetLastWriteTimeUtc(newerMeta, DateTime.UtcNow);

        var result = _discovery.FindLastActiveSession(_tempDir);
        Assert.Equal("newer", result);
    }

    [Fact]
    public void FindLastActiveSession_SingleSession_ReturnsThatSession()
    {
        CreateSessionDir("onlyOne");
        var result = _discovery.FindLastActiveSession(_tempDir);
        Assert.Equal("onlyOne", result);
    }

    [Fact]
    public void EnsureSessionsDir_DoesNotExist_CreatesDirectory()
    {
        var workingDir = Path.Combine(_tempDir, "work1");
        Directory.CreateDirectory(workingDir);
        _discovery.EnsureSessionsDir(workingDir);

        var sessionsDir = Path.Combine(workingDir, ".sessions");
        Assert.True(Directory.Exists(sessionsDir));
    }

    [Fact]
    public void EnsureSessionsDir_AlreadyExists_DoesNotThrow()
    {
        var workingDir = Path.Combine(_tempDir, "work2");
        Directory.CreateDirectory(workingDir);
        var sessionsDir = Path.Combine(workingDir, ".sessions");
        Directory.CreateDirectory(sessionsDir);

        _discovery.EnsureSessionsDir(workingDir);
        Assert.True(Directory.Exists(sessionsDir));
    }

    [Fact]
    public void TouchSessionMeta_NewSession_CreatesMetaFile()
    {
        var workingDir = Path.Combine(_tempDir, "work3");
        Directory.CreateDirectory(workingDir);
        _discovery.EnsureSessionsDir(workingDir);

        _discovery.TouchSessionMeta(workingDir, "testSession");

        var metaPath = Path.Combine(workingDir, ".sessions", "testSession", "session_meta.json");
        Assert.True(File.Exists(metaPath));

        var json = File.ReadAllText(metaPath);
        Assert.Contains("testSession", json);
    }

    [Fact]
    public void TouchSessionMeta_ExistingSession_UpdatesMetaFile()
    {
        var workingDir = Path.Combine(_tempDir, "work4");
        Directory.CreateDirectory(workingDir);
        _discovery.EnsureSessionsDir(workingDir);

        _discovery.TouchSessionMeta(workingDir, "session1");
        var firstWrite = File.GetLastWriteTimeUtc(Path.Combine(workingDir, ".sessions", "session1", "session_meta.json"));

        Thread.Sleep(50);
        _discovery.TouchSessionMeta(workingDir, "session1");
        var secondWrite = File.GetLastWriteTimeUtc(Path.Combine(workingDir, ".sessions", "session1", "session_meta.json"));

        Assert.True(secondWrite >= firstWrite);
    }

    [Fact]
    public void TouchSessionMeta_CreatesSessionDirectory()
    {
        var workingDir = Path.Combine(_tempDir, "work5");
        Directory.CreateDirectory(workingDir);

        _discovery.TouchSessionMeta(workingDir, "newSession");

        var sessionDir = Path.Combine(workingDir, ".sessions", "newSession");
        Assert.True(Directory.Exists(sessionDir));
    }

    [Fact]
    public void MigrateLegacyTranscript_NoLegacyFile_DoesNothing()
    {
        var workingDir = Path.Combine(_tempDir, "work6");
        Directory.CreateDirectory(workingDir);
        _discovery.EnsureSessionsDir(workingDir);
        Directory.CreateDirectory(Path.Combine(workingDir, ".sessions", "main"));

        _discovery.MigrateLegacyTranscript(workingDir);

        var targetPath = Path.Combine(workingDir, ".sessions", "main", "transcript.json");
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public void MigrateLegacyTranscript_WithLegacyFile_CopiesToMainSession()
    {
        var workingDir = Path.Combine(_tempDir, "work7");
        Directory.CreateDirectory(workingDir);
        var legacyPath = Path.Combine(workingDir, "transcript.json");
        File.WriteAllText(legacyPath, "[{\"entry\":\"test\"}]");

        _discovery.EnsureSessionsDir(workingDir);
        Directory.CreateDirectory(Path.Combine(workingDir, ".sessions", "main"));

        _discovery.MigrateLegacyTranscript(workingDir);

        var targetPath = Path.Combine(workingDir, ".sessions", "main", "transcript.json");
        Assert.True(File.Exists(targetPath));
        Assert.Equal("[{\"entry\":\"test\"}]", File.ReadAllText(targetPath));
    }

    [Fact]
    public void MigrateLegacyTranscript_TargetAlreadyExists_DoesNotOverwrite()
    {
        var workingDir = Path.Combine(_tempDir, "work8");
        Directory.CreateDirectory(workingDir);

        var legacyPath = Path.Combine(workingDir, "transcript.json");
        File.WriteAllText(legacyPath, "[\"legacy\"]");

        var sessionsDir = Path.Combine(workingDir, ".sessions");
        var mainDir = Path.Combine(sessionsDir, "main");
        Directory.CreateDirectory(mainDir);
        var targetPath = Path.Combine(mainDir, "transcript.json");
        File.WriteAllText(targetPath, "[\"existing\"]");

        _discovery.MigrateLegacyTranscript(workingDir);

        Assert.Equal("[\"existing\"]", File.ReadAllText(targetPath));
    }

    [Fact]
    public void MigrateLegacyTranscript_NoMainSessionDir_DoesNothing()
    {
        var workingDir = Path.Combine(_tempDir, "work9");
        Directory.CreateDirectory(workingDir);
        var legacyPath = Path.Combine(workingDir, "transcript.json");
        File.WriteAllText(legacyPath, "[\"legacy\"]");

        // Don't create main session dir
        _discovery.EnsureSessionsDir(workingDir);

        _discovery.MigrateLegacyTranscript(workingDir);

        // Legacy file should still exist, nothing migrated
        Assert.True(File.Exists(legacyPath));
    }
}