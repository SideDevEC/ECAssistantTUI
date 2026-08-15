using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Tools.Research;

namespace ECAssistant.Tests.Tools;

public class EFileResearchToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly EAgentConfig _config = new();

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
        return new EFileResearchTool(_fileSystem.Object, _config);
    }


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

    

    // ── ExecuteAsync — no files ──

    [Fact]
    public async Task ExecuteAsync_NoFiles_ReturnsZeroFiles()
    {
        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Returns(Array.Empty<string>());
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("Found 0 files", result.Succeeded ? result.Output : result.Error);
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

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "class" });

        Assert.Contains("test.cs", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("public class Test", result.Succeeded ? result.Output : result.Error);
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

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("a.cs", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("b.txt", result.Succeeded ? result.Output : result.Error);
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

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("truncated", result.Succeeded ? result.Output : result.Error);
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

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test", ["max_files"] = "2" });

        Assert.Contains("Found 2 files", result.Succeeded ? result.Output : result.Error);
    }

    // ── ExecuteAsync — cancellation ──

    [Fact]
    public async Task ExecuteAsync_Cancelled_ReturnsCancelledMessage()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" }, cts.Token);

        Assert.Contains("CANCELLED", result.Error);
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

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test", ["extensions"] = ".py" });

        Assert.Contains("test.py", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("python code", result.Succeeded ? result.Output : result.Error);
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

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.Contains("Found 1 files", result.Error);
    }

    // ── ExecuteAsync — exception handling ──

    [Fact]
    public async Task ExecuteAsync_UnauthorizedAccess_ReturnsAccessDeniedMessage()
    {
        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Throws(new UnauthorizedAccessException("Access denied"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("Access denied", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_GeneralException_ReturnsErrorMessage()
    {
        _fileSystem.Setup(f => f.ListFiles(_tempDir, It.IsAny<string>()))
                   .Throws(new InvalidOperationException("boom"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("Error:", result.Error);
        Assert.Contains("boom", result.Error);
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

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("Error reading", result.Error);
        Assert.Contains("good content", result.Error);
    }
}