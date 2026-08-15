using ECAssistant;
using ECAssistant.Tools.Example;

namespace ECAssistant.Tests.Tools;

public class EFileAnalyzerTests : IDisposable
{
    private readonly string _tempDir;

    public EFileAnalyzerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"eca-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private EFileAnalyzer CreateTool() => new(_tempDir);

    // ── Name / Description ──

    [Fact]
    public void Name_ReturnsEFileAnalyzer()
    {
        var tool = CreateTool();
        Assert.Equal("EFileAnalyzer", tool.Name);
    }

    [Fact]
    public void Description_ContainsAnalyze()
    {
        var tool = CreateTool();
        Assert.Contains("analyze", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    // ── UsageExample ──

    [Fact]
    public void UsageExample_ContainsFilePath()
    {
        var tool = CreateTool();
        Assert.Contains("filePath", tool.UsageExample);
    }

    // ── GetExtendedSystemPrompt ──

    [Fact]
    public void GetExtendedSystemPrompt_ReturnsNonEmptyString()
    {
        var tool = CreateTool();

        var prompt = tool.GetExtendedSystemPrompt();

        Assert.NotEmpty(prompt);
        Assert.Contains("EFileAnalyzer", prompt);
    }

    // ── GetToolExample ──

    [Fact]
    public void GetToolExample_ReturnsXmlFormat()
    {
        var tool = CreateTool();

        var example = tool.GetToolExample();

        Assert.Contains("<toolcall>", example);
        Assert.Contains("EFileAnalyzer", example);
        Assert.Contains("</toolcall>", example);
    }

    // ── ExecuteAsync — missing filePath ──

    [Fact]
    public async Task ExecuteAsync_MissingFilePath_ReturnsFailure()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.False(result.Succeeded);
        Assert.Contains("Missing 'filePath'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_NullFilePath_ReturnsFailure()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = null
        });

        Assert.False(result.Succeeded);
        Assert.Contains("Missing 'filePath'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyFilePath_ReturnsFailure()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = ""
        });

        Assert.False(result.Succeeded);
        Assert.Contains("Missing 'filePath'", result.Error);
    }

    // ── ExecuteAsync — file not found ──

    [Fact]
    public async Task ExecuteAsync_FileNotFound_ReturnsFailure()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "nonexistent.txt"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("File not found", result.Error);
    }

    // ── ExecuteAsync — success ──

    [Fact]
    public async Task ExecuteAsync_ValidFile_ReturnsSuccessWithContent()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(filePath, "Hello World\nSecond Line");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "test.txt"
        });

        Assert.True(result.Succeeded);
        Assert.Contains("Hello World", result.Output);
        Assert.Contains("test.txt", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ValidFile_ReturnsMetadata()
    {
        var filePath = Path.Combine(_tempDir, "meta.txt");
        File.WriteAllText(filePath, "one two three\nfour five");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "meta.txt"
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata!.ContainsKey("size_bytes"));
        Assert.True(result.Metadata!.ContainsKey("line_count"));
        Assert.True(result.Metadata!.ContainsKey("word_count"));
    }

    [Fact]
    public async Task ExecuteAsync_SmallFile_MetadataCorrect()
    {
        var filePath = Path.Combine(_tempDir, "small.txt");
        File.WriteAllText(filePath, "hello world");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "small.txt"
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Metadata);
        Assert.Equal("2", result.Metadata!["word_count"]);
        // "hello world" split by \n with RemoveEmptyEntries gives 1 line
        Assert.Equal("1", result.Metadata!["line_count"]);
    }

    // ── ExecuteAsync — content preview ──

    [Fact]
    public async Task ExecuteAsync_LongFile_PreviewTruncatedAt500Chars()
    {
        var longContent = new string('A', 600);
        var filePath = Path.Combine(_tempDir, "long.txt");
        File.WriteAllText(filePath, longContent);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "long.txt"
        });

        Assert.True(result.Succeeded);
        // Output should contain preview marker ---
        Assert.Contains("---", result.Output);
        // Should contain exactly 500 A's (the preview)
        Assert.Contains(new string('A', 500), result.Output);
        // Should not contain 600 A's
        Assert.DoesNotContain(new string('A', 600), result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ShortFile_FullContentInPreview()
    {
        var filePath = Path.Combine(_tempDir, "short.txt");
        File.WriteAllText(filePath, "short content");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "short.txt"
        });

        Assert.True(result.Succeeded);
        Assert.Contains("short content", result.Output);
        Assert.Contains("---", result.Output);
    }

    // ── ExecuteAsync — subdirectory paths ──

    [Fact]
    public async Task ExecuteAsync_FileInSubdirectory_ReturnsSuccess()
    {
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "nested.txt");
        File.WriteAllText(filePath, "nested content");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "sub/nested.txt"
        });

        Assert.True(result.Succeeded);
        Assert.Contains("nested content", result.Output);
    }

    // ── ExecuteAsync — exception ──

    [Fact]
    public async Task ExecuteAsync_DirectoryAsFilePath_ReturnsFileNotFound()
    {
        // File.Exists returns false for directories
        var dirPath = Path.Combine(_tempDir, "notfile.txt");
        Directory.CreateDirectory(dirPath);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "notfile.txt"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("File not found", result.Error);
    }

    // ── CancellationToken ──

    [Fact]
    public async Task ExecuteAsync_WithCancellationToken_CompletesNormally()
    {
        var filePath = Path.Combine(_tempDir, "cancel.txt");
        File.WriteAllText(filePath, "content");
        var tool = CreateTool();
        var cts = new CancellationTokenSource();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>
        {
            ["filePath"] = "cancel.txt"
        }, cts.Token);

        Assert.True(result.Succeeded);
    }
}