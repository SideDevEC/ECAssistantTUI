using ECAssistant.Tools;

namespace ECAssistant.Tests.Tools;

public class ToolPolicyTests
{
    // ── Default permissions ──

    [Fact]
    public void Constructor_SetsDefaultPermissions()
    {
        var policy = new ToolPolicy();

        Assert.True(policy.IsAllowed("EFileResearchTool"));
        Assert.True(policy.IsAllowed("EFileAnalyzer"));
        Assert.True(policy.IsAllowed("EShellAgent"));
    }

    [Fact]
    public void GetPermissionLevel_UnknownTool_ReturnsAllowed()
    {
        var policy = new ToolPolicy();

        Assert.Equal(ToolPermissionLevel.Allowed, policy.GetPermissionLevel("UnknownTool"));
    }

    // ── SetPermission ──

    [Fact]
    public void SetPermission_Blocked_SetsBlockedLevel()
    {
        var policy = new ToolPolicy();

        policy.SetPermission("MyTool", ToolPermissionLevel.Blocked, "dangerous");

        Assert.Equal(ToolPermissionLevel.Blocked, policy.GetPermissionLevel("MyTool"));
        Assert.True(policy.IsBlocked("MyTool"));
    }

    [Fact]
    public void SetPermission_ApprovalRequired_SetsApprovalLevel()
    {
        var policy = new ToolPolicy();

        policy.SetPermission("MyTool", ToolPermissionLevel.ApprovalRequired);

        Assert.Equal(ToolPermissionLevel.ApprovalRequired, policy.GetPermissionLevel("MyTool"));
        Assert.True(policy.RequiresApproval("MyTool"));
    }

    [Fact]
    public void SetPermission_Allowed_SetsAllowedLevel()
    {
        var policy = new ToolPolicy();
        policy.SetPermission("MyTool", ToolPermissionLevel.Blocked);
        policy.SetPermission("MyTool", ToolPermissionLevel.Allowed);

        Assert.True(policy.IsAllowed("MyTool"));
    }

    [Fact]
    public void SetPermission_OverwritesExistingPermission()
    {
        var policy = new ToolPolicy();
        policy.SetPermission("EShellAgent", ToolPermissionLevel.Blocked, "test");

        Assert.True(policy.IsBlocked("EShellAgent"));
        Assert.False(policy.IsAllowed("EShellAgent"));
    }

    // ── IsAllowed / RequiresApproval / IsBlocked / IsUsable ──

    [Fact]
    public void IsAllowed_BlockedTool_ReturnsFalse()
    {
        var policy = new ToolPolicy();
        policy.SetPermission("T", ToolPermissionLevel.Blocked);

        Assert.False(policy.IsAllowed("T"));
    }

    [Fact]
    public void RequiresApproval_AllowedTool_ReturnsFalse()
    {
        var policy = new ToolPolicy();

        Assert.False(policy.RequiresApproval("EShellAgent"));
    }

    [Fact]
    public void IsBlocked_AllowedTool_ReturnsFalse()
    {
        var policy = new ToolPolicy();

        Assert.False(policy.IsBlocked("EShellAgent"));
    }

    [Fact]
    public void IsUsable_BlockedTool_ReturnsFalse()
    {
        var policy = new ToolPolicy();
        policy.SetPermission("T", ToolPermissionLevel.Blocked);

        Assert.False(policy.IsUsable("T"));
    }

    [Fact]
    public void IsUsable_AllowedTool_ReturnsTrue()
    {
        var policy = new ToolPolicy();

        Assert.True(policy.IsUsable("EShellAgent"));
    }

    [Fact]
    public void IsUsable_ApprovalRequiredTool_ReturnsTrue()
    {
        var policy = new ToolPolicy();
        policy.SetPermission("T", ToolPermissionLevel.ApprovalRequired);

        Assert.True(policy.IsUsable("T"));
    }

    // ── GetAllPermissions ──

    [Fact]
    public void GetAllPermissions_ReturnsAllRegisteredTools()
    {
        var policy = new ToolPolicy();

        var all = policy.GetAllPermissions();

        Assert.Contains(all, p => p.ToolName == "EFileResearchTool");
        Assert.Contains(all, p => p.ToolName == "EFileAnalyzer");
        Assert.Contains(all, p => p.ToolName == "EShellAgent");
    }

    [Fact]
    public void GetAllPermissions_AfterSetPermission_IncludesNewTool()
    {
        var policy = new ToolPolicy();
        policy.SetPermission("NewTool", ToolPermissionLevel.Allowed);

        var all = policy.GetAllPermissions();

        Assert.Contains(all, p => p.ToolName == "NewTool");
    }

    // ── Check ──

    [Fact]
    public void Check_AllowedTool_ReturnsCanExecute()
    {
        var policy = new ToolPolicy();
        var args = new Dictionary<string, string?>();

        var decision = policy.Check("EShellAgent", args);

        Assert.True(decision.CanExecute);
        Assert.False(decision.NeedsApproval);
        Assert.Equal("Allowed", decision.Message);
    }

    [Fact]
    public void Check_BlockedTool_ReturnsCannotExecute()
    {
        var policy = new ToolPolicy();
        policy.SetPermission("T", ToolPermissionLevel.Blocked);
        var args = new Dictionary<string, string?>();

        var decision = policy.Check("T", args);

        Assert.False(decision.CanExecute);
        Assert.False(decision.NeedsApproval);
        Assert.Contains("blocked", decision.Message);
    }

    [Fact]
    public void Check_ApprovalRequiredTool_ReturnsNeedsApproval()
    {
        var policy = new ToolPolicy();
        policy.SetPermission("T", ToolPermissionLevel.ApprovalRequired);
        var args = new Dictionary<string, string?>();

        var decision = policy.Check("T", args);

        Assert.False(decision.CanExecute);
        Assert.True(decision.NeedsApproval);
        Assert.Contains("requires approval", decision.Message);
    }

    [Fact]
    public void Check_UnknownTool_ReturnsCanExecute()
    {
        var policy = new ToolPolicy();
        var args = new Dictionary<string, string?>();

        var decision = policy.Check("UnknownTool", args);

        Assert.True(decision.CanExecute);
    }

    // ── LoadFromConfig ──

    [Fact]
    public void LoadFromConfig_NullEntries_DoesNothing()
    {
        var policy = new ToolPolicy();

        policy.LoadFromConfig(null);

        // defaults remain unchanged
        Assert.True(policy.IsAllowed("EShellAgent"));
    }

    [Fact]
    public void LoadFromConfig_EmptyList_DoesNothing()
    {
        var policy = new ToolPolicy();

        policy.LoadFromConfig(new List<ToolPermissionConfigEntry>());

        Assert.True(policy.IsAllowed("EShellAgent"));
    }

    [Fact]
    public void LoadFromConfig_ValidEntries_SetsPermissions()
    {
        var policy = new ToolPolicy();
        var entries = new List<ToolPermissionConfigEntry>
        {
            new() { ToolName = "ToolA", Level = "Blocked", Reason = "danger" },
            new() { ToolName = "ToolB", Level = "ApprovalRequired", Reason = "needs review" },
            new() { ToolName = "ToolC", Level = "Allowed", Reason = "safe" },
        };

        policy.LoadFromConfig(entries);

        Assert.True(policy.IsBlocked("ToolA"));
        Assert.True(policy.RequiresApproval("ToolB"));
        Assert.True(policy.IsAllowed("ToolC"));
    }

    [Fact]
    public void LoadFromConfig_InvalidLevel_SkipsEntry()
    {
        var policy = new ToolPolicy();
        var entries = new List<ToolPermissionConfigEntry>
        {
            new() { ToolName = "BadTool", Level = "InvalidLevel" },
        };

        policy.LoadFromConfig(entries);

        // Not set, so falls back to default Allowed
        Assert.True(policy.IsAllowed("BadTool"));
    }

    [Fact]
    public void LoadFromConfig_CaseInsensitiveLevel_ParsesCorrectly()
    {
        var policy = new ToolPolicy();
        var entries = new List<ToolPermissionConfigEntry>
        {
            new() { ToolName = "T", Level = "blocked" },
        };

        policy.LoadFromConfig(entries);

        Assert.True(policy.IsBlocked("T"));
    }
}