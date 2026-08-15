using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Tools.Reader;

namespace ECAssistant.Tests.Tools;

public class EFileReaderToolTests
{
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly EAgentConfig _config = new();

    private EFileReaderTool CreateTool(string workingDir = "/project")
    {
        return new EFileReaderTool(_fileSystem.Object, _config);
    }


    [Fact]
    public void Name_ReturnsEFileReader()
    {
        var tool = CreateTool();
        Assert.Equal("EFileReader", tool.Name);
    }

    [Fact]
    public void Description_ContainsRead()
    {
        var tool = CreateTool();
        Assert.Contains("read", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    

    // ── ExecuteAsync — missing file ──

    [Fact]
    public async Task ExecuteAsync_MissingFile_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.Contains("Missing required argument: file", result.Error);
    }

    // ── ExecuteAsync — file not found ──

    [Fact]
    public async Task ExecuteAsync_FileNotFound_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "nofile.txt" });

        Assert.Contains("not found", result.Error);
    }

    // ── ExecuteAsync — success ──

    [Fact]
    public async Task ExecuteAsync_ValidFile_ReturnsContentWithLineNumbers()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2\nline3");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "test.txt" });

        Assert.Contains("test.txt", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("1", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("line1", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("line2", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("line3", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsTotalLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("a\nb\nc\nd\ne");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "test.txt" });

        Assert.Contains("TotalLines: 5", result.Succeeded ? result.Output : result.Error);
    }

    // ── ExecuteAsync — offset ──

    [Fact]
    public async Task ExecuteAsync_WithOffset_StartsFromSpecifiedLine()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2\nline3\nline4\nline5");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "test.txt", ["offset"] = "3" });

        Assert.Contains("line3", result.Succeeded ? result.Output : result.Error);
        Assert.DoesNotContain("line1", result.Succeeded ? result.Output : result.Error);
        Assert.DoesNotContain("line2", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_OffsetBeyondEnd_ReturnsOffsetBeyondMessage()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "test.txt", ["offset"] = "10" });

        Assert.Contains("beyond end of file", result.Succeeded ? result.Output : result.Error);
    }

    // ── ExecuteAsync — limit ──

    [Fact]
    public async Task ExecuteAsync_WithLimit_ReturnsLimitedLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2\nline3\nline4\nline5");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "test.txt", ["limit"] = "2" });

        Assert.Contains("line1", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("line2", result.Succeeded ? result.Output : result.Error);
        Assert.DoesNotContain("line3", result.Succeeded ? result.Output : result.Error);
    }

    // ── ExecuteAsync — maxchars ──

    [Fact]
    public async Task ExecuteAsync_WithMaxChars_TruncatesOutput()
    {
        var longLine = new string('X', 200);
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns($"{longLine}\n{longLine}\n{longLine}");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "test.txt", ["maxchars"] = "100" });

        Assert.Contains("Truncated", result.Succeeded ? result.Output : result.Error);
    }

    // ── ExecuteAsync — more available message ──

    [Fact]
    public async Task ExecuteAsync_MoreLinesAvailable_ReturnsMoreAvailableMessage()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("a\nb\nc\nd\ne");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "test.txt", ["limit"] = "2" });

        Assert.Contains("More available", result.Succeeded ? result.Output : result.Error);
    }

    // ── ExecuteAsync — exception ──

    [Fact]
    public async Task ExecuteAsync_Exception_ReturnsErrorMessage()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Throws(new IOException("disk error"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "test.txt" });

        Assert.Contains("Error reading file", result.Error);
        Assert.Contains("disk error", result.Error);
    }

    // ── Absolute path ──

    [Fact]
    public async Task ExecuteAsync_AbsolutePath_ResolvedDirectly()
    {
        _fileSystem.Setup(f => f.FileExists("/abs/path/file.txt")).Returns(true);
        _fileSystem.Setup(f => f.ReadFile("/abs/path/file.txt")).Returns("content");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "/abs/path/file.txt" });

        Assert.Contains("content", result.Succeeded ? result.Output : result.Error);
    }

    // ── Empty file ──

    [Fact]
    public async Task ExecuteAsync_EmptyFile_ReturnsEmptyContent()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["file"] = "empty.txt" });

        Assert.Contains("TotalLines: 1", result.Error);
    }
}