using ECAssistant.Session;

namespace ECAssistant.Tests.Integration;

/// <summary>
/// Integration tests for SessionDiscovery — exercises real file system I/O
/// across the full session lifecycle: discovery, creation, migration, and ordering.
/// </summary>
public class SessionManagementIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionDiscovery _discovery;

    public SessionManagementIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAInteg_Session_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _discovery = new SessionDiscovery();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void DiscoverSessions_EmptyDir_ReturnsEmptyList()
    {
        var result = _discovery.DiscoverSessions(_tempDir);
        Assert.Empty(result);
    }

    [Fact]
    public void TouchSessionMeta_CreatesSessionDirAndMeta_DiscoverSessionsFindsIt()
    {
        _discovery.EnsureSessionsDir(_tempDir);
        _discovery.TouchSessionMeta(_tempDir, "main");

        var sessions = _discovery.DiscoverSessions(_tempDir);
        Assert.Single(sessions);
        Assert.Equal("main", sessions[0]);
    }

    [Fact]
    public void TouchSessionMeta_UpdatesLastModified_DiscoverSessionsReturnsNewestFirst()
    {
        _discovery.EnsureSessionsDir(_tempDir);

        // Create "older" session
        _discovery.TouchSessionMeta(_tempDir, "older");
        var olderMeta = Path.Combine(_tempDir, ".sessions", "older", "session_meta.json");
        File.SetLastWriteTimeUtc(olderMeta, DateTime.UtcNow.AddHours(-2));

        // Create "newer" session slightly later
        Thread.Sleep(50);
        _discovery.TouchSessionMeta(_tempDir, "newer");

        var sessions = _discovery.DiscoverSessions(_tempDir);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("newer", sessions[0]);
        Assert.Equal("older", sessions[1]);
    }

    [Fact]
    public void MigrateLegacyTranscript_LegacyFileExists_CopiesToMainSession()
    {
        // Create legacy transcript.json in working dir
        var legacyPath = Path.Combine(_tempDir, "transcript.json");
        var legacyContent = "[{\"Role\":\"user\",\"Content\":\"Hello\"}]";
        File.WriteAllText(legacyPath, legacyContent);

        // Create .sessions/main/ directory
        _discovery.EnsureSessionsDir(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, ".sessions", "main"));

        _discovery.MigrateLegacyTranscript(_tempDir);

        var targetPath = Path.Combine(_tempDir, ".sessions", "main", "transcript.json");
        Assert.True(File.Exists(targetPath));
        Assert.Equal(legacyContent, File.ReadAllText(targetPath));
    }

    [Fact]
    public void MigrateLegacyTranscript_NoLegacyFile_DoesNothing()
    {
        _discovery.EnsureSessionsDir(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, ".sessions", "main"));

        _discovery.MigrateLegacyTranscript(_tempDir);

        var targetPath = Path.Combine(_tempDir, ".sessions", "main", "transcript.json");
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public void MigrateLegacyTranscript_TargetAlreadyExists_DoesNotOverwrite()
    {
        var legacyPath = Path.Combine(_tempDir, "transcript.json");
        File.WriteAllText(legacyPath, "[\"legacy\"]");

        _discovery.EnsureSessionsDir(_tempDir);
        var mainDir = Path.Combine(_tempDir, ".sessions", "main");
        Directory.CreateDirectory(mainDir);
        var targetPath = Path.Combine(mainDir, "transcript.json");
        File.WriteAllText(targetPath, "[\"existing\"]");

        _discovery.MigrateLegacyTranscript(_tempDir);

        Assert.Equal("[\"existing\"]", File.ReadAllText(targetPath));
    }

    [Fact]
    public void EnsureSessionsDir_MissingDir_CreatesDirectory()
    {
        var workingDir = Path.Combine(_tempDir, "workspace1");
        Directory.CreateDirectory(workingDir);

        _discovery.EnsureSessionsDir(workingDir);

        var sessionsDir = Path.Combine(workingDir, ".sessions");
        Assert.True(Directory.Exists(sessionsDir));
    }

    [Fact]
    public void EnsureSessionsDir_AlreadyExists_DoesNotThrow()
    {
        var workingDir = Path.Combine(_tempDir, "workspace2");
        Directory.CreateDirectory(workingDir);
        Directory.CreateDirectory(Path.Combine(workingDir, ".sessions"));

        _discovery.EnsureSessionsDir(workingDir);

        Assert.True(Directory.Exists(Path.Combine(workingDir, ".sessions")));
    }

    [Fact]
    public void FindLastActiveSession_MultipleSessions_ReturnsMostRecent()
    {
        _discovery.EnsureSessionsDir(_tempDir);

        _discovery.TouchSessionMeta(_tempDir, "sessionA");
        var metaA = Path.Combine(_tempDir, ".sessions", "sessionA", "session_meta.json");
        File.SetLastWriteTimeUtc(metaA, DateTime.UtcNow.AddHours(-3));

        Thread.Sleep(50);
        _discovery.TouchSessionMeta(_tempDir, "sessionB");
        Thread.Sleep(50);
        _discovery.TouchSessionMeta(_tempDir, "sessionC");

        var result = _discovery.FindLastActiveSession(_tempDir);
        Assert.Equal("sessionC", result);
    }

    [Fact]
    public void FindLastActiveSession_NoSessions_ReturnsNull()
    {
        var result = _discovery.FindLastActiveSession(_tempDir);
        Assert.Null(result);
    }

    [Fact]
    public void FullSessionLifecycle_CreateDiscoverMigrate_WorksEndToEnd()
    {
        // 1. Ensure sessions dir
        _discovery.EnsureSessionsDir(_tempDir);
        Assert.True(Directory.Exists(Path.Combine(_tempDir, ".sessions")));

        // 2. Initially no sessions
        Assert.Empty(_discovery.DiscoverSessions(_tempDir));

        // 3. Create a legacy transcript
        var legacyPath = Path.Combine(_tempDir, "transcript.json");
        File.WriteAllText(legacyPath, "[{\"Role\":\"user\",\"Content\":\"test\"}]");

        // 4. Create main session dir and migrate
        Directory.CreateDirectory(Path.Combine(_tempDir, ".sessions", "main"));
        _discovery.MigrateLegacyTranscript(_tempDir);

        // 5. Touch main session meta
        _discovery.TouchSessionMeta(_tempDir, "main");

        // 6. Discover sessions — should find "main"
        var sessions = _discovery.DiscoverSessions(_tempDir);
        Assert.Single(sessions);
        Assert.Equal("main", sessions[0]);

        // 7. Find last active — should be "main"
        Assert.Equal("main", _discovery.FindLastActiveSession(_tempDir));

        // 8. Verify migrated transcript exists
        Assert.True(File.Exists(Path.Combine(_tempDir, ".sessions", "main", "transcript.json")));
    }
}