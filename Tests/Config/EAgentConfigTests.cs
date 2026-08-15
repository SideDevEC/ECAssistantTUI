using ECAssistant.Config;

namespace ECAssistant.Tests.Config;

public class EAgentConfigTests : IDisposable
{
    private readonly string _tempDir;

    public EAgentConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAssistantTests_Config_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Constructor_Default_CreatesInstanceWithDefaults()
    {
        var config = new EAgentConfig();
        Assert.NotNull(config);
        Assert.Equal("ECAssistant", config.RootPath);
    }

    [Fact]
    public void Constructor_Default_InitializesAllSections()
    {
        var config = new EAgentConfig();
        Assert.NotNull(config.Memory);
        Assert.NotNull(config.Workspace);
        Assert.NotNull(config.Tools);
        Assert.NotNull(config.Llm);
        Assert.NotNull(config.SecondaryModel);
        Assert.NotNull(config.SubAgent);
        Assert.NotNull(config.VectorMemory);
        Assert.NotNull(config.Inference);
        Assert.NotNull(config.ContextManagement);
        Assert.NotNull(config.Sampling);
        Assert.NotNull(config.AgentSettings);
        Assert.NotNull(config.Interface);
    }

    [Fact]
    public void GetRootPath_DefaultRootPath_ReturnsFullPath()
    {
        var config = new EAgentConfig();
        var rootPath = config.GetRootPath();
        Assert.True(Path.IsPathRooted(rootPath));
        Assert.Contains("ECAssistant", rootPath);
    }

    [Fact]
    public void GetRootPath_CustomRootPath_ReturnsCustomFullPath()
    {
        var config = new EAgentConfig { RootPath = "MyCustomProject" };
        var rootPath = config.GetRootPath();
        Assert.True(Path.IsPathRooted(rootPath));
        Assert.Contains("MyCustomProject", rootPath);
    }

    [Fact]
    public void GetMemoryDirectory_Default_ReturnsMemorySubdirectory()
    {
        var config = new EAgentConfig();
        var memDir = config.GetMemoryDirectory();
        Assert.True(Path.IsPathRooted(memDir));
        Assert.Contains("Memory", memDir);
    }

    [Fact]
    public void GetMemoryDirectory_CustomRootPath_ContainsCustomRoot()
    {
        var config = new EAgentConfig { RootPath = "CustomRoot" };
        var memDir = config.GetMemoryDirectory();
        Assert.Contains("CustomRoot", memDir);
        Assert.Contains("Memory", memDir);
    }

    [Fact]
    public void GetWorkspaceDirectory_Default_ReturnsWorkspaceSubdirectory()
    {
        var config = new EAgentConfig();
        var wsDir = config.GetWorkspaceDirectory();
        Assert.True(Path.IsPathRooted(wsDir));
        Assert.Contains("Workspace", wsDir);
    }

    [Fact]
    public void GetWorkspaceDirectory_CustomRootPath_ContainsCustomRoot()
    {
        var config = new EAgentConfig { RootPath = "CustomRoot" };
        var wsDir = config.GetWorkspaceDirectory();
        Assert.Contains("CustomRoot", wsDir);
        Assert.Contains("Workspace", wsDir);
    }

    [Fact]
    public void Save_ValidFilePath_WritesJsonFile()
    {
        var config = new EAgentConfig { RootPath = "TestProject" };
        var filePath = Path.Combine(_tempDir, "appsettings.json");
        config.Save(filePath);
        Assert.True(File.Exists(filePath));
        var content = File.ReadAllText(filePath);
        Assert.Contains("TestProject", content);
        Assert.Contains("root_path", content);
    }

    [Fact]
    public void Save_DefaultFilePath_WritesToAppsettingsJson()
    {
        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempDir;
            var config = new EAgentConfig { RootPath = "DefaultSave" };
            config.Save();
            Assert.True(File.Exists(Path.Combine(_tempDir, "appsettings.json")));
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
        }
    }

    [Fact]
    public void Save_ProducesValidJson()
    {
        var config = new EAgentConfig { RootPath = "JsonTest" };
        var filePath = Path.Combine(_tempDir, "test_save.json");
        config.Save(filePath);
        var content = File.ReadAllText(filePath);
        // Verify it's valid JSON by parsing it
        using var doc = System.Text.Json.JsonDocument.Parse(content);
        Assert.True(doc.RootElement.TryGetProperty("root_path", out var rp));
        Assert.Equal("JsonTest", rp.GetString());
    }

    [Fact]
    public void Save_Roundtrip_PreservesConfiguration()
    {
        var config = new EAgentConfig
        {
            RootPath = "RoundtripTest",
            Memory = new MemoryConfig { DataPath = "CustomMemory", Enabled = false }
        };
        var filePath = Path.Combine(_tempDir, "roundtrip.json");
        config.Save(filePath);

        var loaded = System.Text.Json.JsonSerializer.Deserialize<EAgentConfig>(File.ReadAllText(filePath));
        Assert.NotNull(loaded);
        Assert.Equal("RoundtripTest", loaded.RootPath);
        Assert.Equal("CustomMemory", loaded.Memory.DataPath);
        Assert.False(loaded.Memory.Enabled);
    }
}