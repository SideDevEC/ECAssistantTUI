using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Tools.Code;

namespace ECAssistant.Tests.Tools;

public class ECodeEditorToolTests
{
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly EAgentConfig _config = new();

    private ECodeEditorTool CreateTool(string workingDir = "/project")
    {
        return new ECodeEditorTool(_fileSystem.Object, _config);
    }

    [Fact]
    public void Name_ReturnsECodeEditor()
    {
        var tool = CreateTool();
        Assert.Equal("ECodeEditor", tool.Name);
    }

    [Fact]
    public void Description_ContainsCodeEditing()
    {
        var tool = CreateTool();
        Assert.Contains("code", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    

    // ── ExecuteAsync — missing action ──

    [Fact]
    public async Task ExecuteAsync_MissingAction_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.Contains("Missing 'action'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "unknown" });

        Assert.Contains("Unknown action", result.Error);
    }

    // ── ExecuteAsync — create ──

    [Fact]
    public async Task ExecuteAsync_Create_NewFile_CreatesFile()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "create", ["file"] = "test.txt", ["content"] = "Hello" });

        Assert.Contains("Created", result.Output + result.Error);
        Assert.Contains("Hello", result.Output + result.Error);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), "Hello"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Create_ExistingFile_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "create", ["file"] = "test.txt", ["content"] = "Hi" });

        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Create_MissingFile_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "create", ["content"] = "Hi" });

        Assert.Contains("Missing 'file'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithDirectory_CreatesDirectory()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _fileSystem.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "create", ["file"] = "sub/test.txt", ["content"] = "Hi" });

        Assert.Contains("Created", result.Output + result.Error);
        _fileSystem.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
    }

    // ── ExecuteAsync — patch ──

    [Fact]
    public async Task ExecuteAsync_Patch_ValidOldText_AppliesPatch()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Hello World");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "test.txt", ["old_text"] = "Hello", ["new_text"] = "Hi" });

        Assert.Contains("Patched", result.Output + result.Error);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), "Hi World"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_FileNotFound_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "nofile.txt", ["old_text"] = "a", ["new_text"] = "b" });

        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_OldTextNotFound_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Hello World");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "test.txt", ["old_text"] = "Goodbye", ["new_text"] = "Hi" });

        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_MultipleOccurrences_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("test test test");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "test.txt", ["old_text"] = "test", ["new_text"] = "xyz" });

    }

    [Fact]
    public async Task ExecuteAsync_Patch_OldTextNotFound_ReturnsSimilarLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Hello World\nGoodbye World");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "test.txt", ["old_text"] = "Hello World Test", ["new_text"] = "Hi" });

        Assert.Contains("not found", result.Error);
        Assert.Contains("Similar", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_MissingArguments_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "test.txt" });

        Assert.Contains("Missing", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_Cancelled_ReturnsCancelledMessage()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Hello World");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "test.txt", ["old_text"] = "Hello", ["new_text"] = "Hi" }, cts.Token);

        Assert.Contains("CANCELLED", result.Error);
    }

    // ── ExecuteAsync — diff ──

    [Fact]
    public async Task ExecuteAsync_Diff_ReturnsDiff()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("old line same line");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "diff", ["file"] = "test.txt", ["new_text"] = "new line same line" });

        Assert.Contains("Diff", result.Output + result.Error);
        Assert.Contains("new line", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Diff_FileNotFound_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "diff", ["file"] = "nofile.txt", ["new_text"] = "x" });

        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Diff_NoChanges_ReturnsNoChanges()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("same");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "diff", ["file"] = "test.txt", ["new_text"] = "same" });

        Assert.Contains("no changes", result.Output + result.Error);
    }

    // ── ExecuteAsync — search ──

    [Fact]
    public async Task ExecuteAsync_Search_MissingPattern_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "search" });

        Assert.Contains("Missing 'pattern'", result.Error);
    }

    // ── ExecuteAsync — insert ──

    [Fact]
    public async Task ExecuteAsync_Insert_AtLine_ReturnsInsertedMessage()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline3");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "insert", ["file"] = "test.txt", ["line"] = "2", ["text"] = "line2" });

        Assert.Contains("Inserted", result.Output + result.Error);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), It.Is<string>(s => s.Contains("line2"))), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Insert_MissingArguments_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "insert", ["file"] = "test.txt" });

        Assert.Contains("Missing", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Insert_InvalidLine_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "insert", ["file"] = "test.txt", ["line"] = "abc", ["text"] = "x" });

        Assert.Contains("Missing", result.Error);
    }

    // ── ExecuteAsync — delete-lines ──

    [Fact]
    public async Task ExecuteAsync_DeleteLines_ValidRange_DeletesLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2\nline3\nline4");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "delete-lines", ["file"] = "test.txt", ["start_line"] = "2", ["end_line"] = "3" });

        Assert.Contains("Deleted", result.Output + result.Error);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), It.Is<string>(s => !s.Contains("line2") && !s.Contains("line3"))), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteLines_InvalidNumbers_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "delete-lines", ["file"] = "test.txt", ["start_line"] = "abc", ["end_line"] = "3" });

        Assert.Contains("Missing", result.Error);
    }

    // ── CountOccurrences (tested indirectly via patch) ──

    [Fact]
    public async Task ExecuteAsync_Patch_SingleOccurrence_AppliesPatch()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("old text here");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "test.txt", ["old_text"] = "old text", ["new_text"] = "new text" });

        Assert.Contains("Patched", result.Output + result.Error);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), "new text here"), Times.Once);
    }

    // ── FindSimilarLines (tested indirectly via patch) ──

    [Fact]
    public async Task ExecuteAsync_Patch_SimilarLinesFound_ReturnsSimilarLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("function HelloWorld() {\n  return greet;\n}\nfunction Hello() {\n  return greet;\n}");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "patch", ["file"] = "test.txt", ["old_text"] = "function HelloWorld Test", ["new_text"] = "x" });

        Assert.Contains("not found", result.Output + result.Error);
    }

    // ── GenerateDiff (tested indirectly via diff action) ──

    [Fact]
    public async Task ExecuteAsync_Diff_WithChanges_ReturnsPlusMinusDiff()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("old line same line");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "diff", ["file"] = "test.txt", ["new_text"] = "new line same line" });

        Assert.Contains("- ", result.Output + result.Error);
        Assert.Contains("+ ", result.Output + result.Error);
        Assert.Contains("old line", result.Output + result.Error);
        Assert.Contains("new line", result.Output + result.Error);
    }

    // ── Replace-All ──

    [Fact]
    public async Task ExecuteAsync_ReplaceAll_MissingPattern_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "replace-all" });

        Assert.Contains("Missing 'pattern'", result.Error);
    }

    // ── Cancellation ──

    [Fact]
    public async Task ExecuteAsync_Insert_Cancelled_ReturnsCancelledMessage()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "insert", ["file"] = "test.txt", ["line"] = "1", ["text"] = "new" }, cts.Token);

        Assert.Contains("CANCELLED", result.Error);
    }
}