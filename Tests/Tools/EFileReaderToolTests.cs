using ECAssistant.Interfaces;
using ECAssistant.Tools.Reader;

namespace ECAssistant.Tests.Tools;

public class EFileReaderToolTests
{
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly Mock<IConfigProvider> _configProvider = new();

    private EFileReaderTool CreateTool(string workingDir = "/project")
    {
        _configProvider.Setup(c => c.GetValue("workingDir", It.IsAny<string>()))
                       .Returns(workingDir);
        return new EFileReaderTool(_fileSystem.Object, _configProvider.Object);
    }

    // ── Name / Description / GetPolicy ──

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

    [Fact]
    public void GetPolicy_ReturnsAllowedPolicy()
    {
        var tool = CreateTool();
        var policy = tool.GetPolicy();

        Assert.Equal("EFileReader", policy.ToolName);
        Assert.Equal("Allowed", policy.Level);
    }

    // ── ExecuteAsync — missing file ──

    [Fact]
    public async Task ExecuteAsync_MissingFile_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("");

        Assert.Contains("Missing required argument: file", result);
    }

    // ── ExecuteAsync — file not found ──

    [Fact]
    public async Task ExecuteAsync_FileNotFound_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>nofile.txt</file>");

        Assert.Contains("not found", result);
    }

    // ── ExecuteAsync — success ──

    [Fact]
    public async Task ExecuteAsync_ValidFile_ReturnsContentWithLineNumbers()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2\nline3");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>test.txt</file>");

        Assert.Contains("test.txt", result);
        Assert.Contains("1", result);
        Assert.Contains("line1", result);
        Assert.Contains("line2", result);
        Assert.Contains("line3", result);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsTotalLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("a\nb\nc\nd\ne");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>test.txt</file>");

        Assert.Contains("TotalLines: 5", result);
    }

    // ── ExecuteAsync — offset ──

    [Fact]
    public async Task ExecuteAsync_WithOffset_StartsFromSpecifiedLine()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2\nline3\nline4\nline5");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>test.txt</file><offset>3</offset>");

        Assert.Contains("line3", result);
        Assert.DoesNotContain("line1", result);
        Assert.DoesNotContain("line2", result);
    }

    [Fact]
    public async Task ExecuteAsync_OffsetBeyondEnd_ReturnsOffsetBeyondMessage()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>test.txt</file><offset>10</offset>");

        Assert.Contains("beyond end of file", result);
    }

    // ── ExecuteAsync — limit ──

    [Fact]
    public async Task ExecuteAsync_WithLimit_ReturnsLimitedLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2\nline3\nline4\nline5");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>test.txt</file><limit>2</limit>");

        Assert.Contains("line1", result);
        Assert.Contains("line2", result);
        Assert.DoesNotContain("line3", result);
    }

    // ── ExecuteAsync — maxchars ──

    [Fact]
    public async Task ExecuteAsync_WithMaxChars_TruncatesOutput()
    {
        var longLine = new string('X', 200);
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns($"{longLine}\n{longLine}\n{longLine}");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>test.txt</file><maxchars>100</maxchars>");

        Assert.Contains("Truncated", result);
    }

    // ── ExecuteAsync — more available message ──

    [Fact]
    public async Task ExecuteAsync_MoreLinesAvailable_ReturnsMoreAvailableMessage()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("a\nb\nc\nd\ne");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>test.txt</file><limit>2</limit>");

        Assert.Contains("More available", result);
    }

    // ── ExecuteAsync — exception ──

    [Fact]
    public async Task ExecuteAsync_Exception_ReturnsErrorMessage()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Throws(new IOException("disk error"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>test.txt</file>");

        Assert.Contains("Error reading file", result);
        Assert.Contains("disk error", result);
    }

    // ── Absolute path ──

    [Fact]
    public async Task ExecuteAsync_AbsolutePath_ResolvedDirectly()
    {
        _fileSystem.Setup(f => f.FileExists("/abs/path/file.txt")).Returns(true);
        _fileSystem.Setup(f => f.ReadFile("/abs/path/file.txt")).Returns("content");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>/abs/path/file.txt</file>");

        Assert.Contains("content", result);
    }

    // ── Empty file ──

    [Fact]
    public async Task ExecuteAsync_EmptyFile_ReturnsEmptyContent()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<file>empty.txt</file>");

        Assert.Contains("TotalLines: 1", result);
    }
}