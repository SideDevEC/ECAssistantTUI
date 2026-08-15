using ECAssistant.Interfaces;
using ECAssistant.Tools.Research;

namespace ECAssistant.Tests.Tools;

public class EFileResearchToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly Mock<IConfigProvider> _configProvider = new();
    private readonly Mock<IColorFormatter> _colorFormatter = new();

    public EFileResearchToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"eca-research-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private EFileResearchTool CreateTool()
    {
        _configProvider.Setup(c => c.GetValue("searchRoot", It.IsAny<string>()))
                       .Returns(_tempDir);
        _configProvider.Setup(c => c.GetValue("researchExtensions", It.IsAny<string>()))
                       .Returns(".cs,.txt,.md");
        _configProvider.Setup(c => c.GetInt("maxCharsPerFile", It.IsAny<int>()))
                       .Returns(10000);
        return new EFileResearchTool(_fileSystem.Object, _configProvider.Object, _colorFormatter.Object);
    }

    // ── Name / Description / GetPolicy ──

    [Fact]
    public void Name_ReturnsEFileResearchTool()
    {
        var tool = CreateTool();
        Assert.Equal("EFileResearchTool", tool.Name);
    }

    [Fact]
    public void Description_ContainsScan()
    {
        var tool = CreateTool();
        Assert.Contains("scan", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPolicy_ReturnsAllowedPolicy()
    {
        var tool = CreateTool();
        var policy = tool.GetPolicy();

        Assert.Equal("EFileResearchTool", policy.ToolName);
        Assert.Equal("Allowed", policy.Level);
    }

    // ── ExecuteAsync — no files ──

    [Fact]
    public async Task ExecuteAsync_NoFiles_ReturnsZeroFiles()
    {
        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(Array.Empty<string>());
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query>");

        Assert.Contains("Found 0 files", result);
    }

    [Fact]
    public async Task ExecuteAsync_WithFiles_ReturnsContent()
    {
        var filePath = Path.Combine(_tempDir, "test.cs");
        File.WriteAllText(filePath, "public class Test { }");

        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(new[] { filePath });
        _fileSystem.Setup(f => f.ReadFile(filePath))
                   .Returns("public class Test { }");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>class</query>");

        Assert.Contains("test.cs", result);
        Assert.Contains("public class Test", result);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleFiles_ReturnsAll()
    {
        var fileA = Path.Combine(_tempDir, "a.cs");
        var fileB = Path.Combine(_tempDir, "b.txt");
        File.WriteAllText(fileA, "content a");
        File.WriteAllText(fileB, "content b");

        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(new[] { fileA, fileB });
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>()))
                   .Returns("content");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query>");

        Assert.Contains("a.cs", result);
        Assert.Contains("b.txt", result);
    }

    [Fact]
    public async Task ExecuteAsync_LargeFile_TruncatesContent()
    {
        var bigContent = new string('X', 20000);
        var filePath = Path.Combine(_tempDir, "big.txt");
        File.WriteAllText(filePath, bigContent);

        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(new[] { filePath });
        _fileSystem.Setup(f => f.ReadFile(filePath))
                   .Returns(bigContent);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query>");

        Assert.Contains("truncated", result);
    }

    // ── ExecuteAsync — max_files ──

    [Fact]
    public async Task ExecuteAsync_MaxFiles_LimitsResults()
    {
        var files = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var p = Path.Combine(_tempDir, $"file{i}.cs");
            File.WriteAllText(p, "content");
            files.Add(p);
        }

        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(files.ToArray());
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>()))
                   .Returns("content");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query><max_files>2</max_files>");

        Assert.Contains("Found 2 files", result);
    }

    // ── ExecuteAsync — cancellation ──

    [Fact]
    public async Task ExecuteAsync_Cancelled_ReturnsCancelledMessage()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query>", cts.Token);

        Assert.Contains("CANCELLED", result);
    }

    // ── ExecuteAsync — custom extensions ──

    [Fact]
    public async Task ExecuteAsync_CustomExtensions_FiltersCorrectly()
    {
        var pyFile = Path.Combine(_tempDir, "test.py");
        File.WriteAllText(pyFile, "python code");

        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(new[] { pyFile });
        _fileSystem.Setup(f => f.ReadFile(pyFile))
                   .Returns("python code");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query><extensions>.py</extensions>");

        Assert.Contains("test.py", result);
        Assert.Contains("python code", result);
    }

    // ── ExecuteAsync — empty query ──

    [Fact]
    public async Task ExecuteAsync_EmptyQuery_StillReturnsResults()
    {
        var filePath = Path.Combine(_tempDir, "test.cs");
        File.WriteAllText(filePath, "content");

        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(new[] { filePath });
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>()))
                   .Returns("content");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("");

        Assert.Contains("Found 1 files", result);
    }

    // ── ExecuteAsync — exception handling ──

    [Fact]
    public async Task ExecuteAsync_UnauthorizedAccess_ReturnsAccessDeniedMessage()
    {
        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Throws(new UnauthorizedAccessException("Access denied"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query>");

        Assert.Contains("Access denied", result);
    }

    [Fact]
    public async Task ExecuteAsync_GeneralException_ReturnsErrorMessage()
    {
        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Throws(new InvalidOperationException("boom"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query>");

        Assert.Contains("Error:", result);
        Assert.Contains("boom", result);
    }

    // ── ExecuteAsync — file read error ──

    [Fact]
    public async Task ExecuteAsync_FileReadError_ContinuesAndReportsError()
    {
        var badFile = Path.Combine(_tempDir, "bad.cs");
        var goodFile = Path.Combine(_tempDir, "good.cs");
        File.WriteAllText(badFile, "bad");
        File.WriteAllText(goodFile, "good content");

        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(new[] { badFile, goodFile });
        _fileSystem.Setup(f => f.ReadFile(badFile))
                   .Throws(new IOException("read error"));
        _fileSystem.Setup(f => f.ReadFile(goodFile))
                   .Returns("good content");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<query>test</query>");

        Assert.Contains("Error reading", result);
        Assert.Contains("good content", result);
    }
}