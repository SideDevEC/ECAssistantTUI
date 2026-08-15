using ECAssistant.Interfaces;
using ECAssistant.Tools.Code;

namespace ECAssistant.Tests.Tools;

public class ECodeEditorToolTests
{
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly Mock<IConfigProvider> _configProvider = new();
    private readonly Mock<IColorFormatter> _colorFormatter = new();

    private ECodeEditorTool CreateTool(string workingDir = "/project")
    {
        _configProvider.Setup(c => c.GetValue("workingDir", It.IsAny<string>()))
                       .Returns(workingDir);
        return new ECodeEditorTool(_fileSystem.Object, _configProvider.Object, _colorFormatter.Object);
    }

    // ── Name / Description / GetPolicy ──

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

    [Fact]
    public void GetPolicy_ReturnsApprovedPolicy()
    {
        var tool = CreateTool();
        var policy = tool.GetPolicy();

        Assert.Equal("ECodeEditor", policy.ToolName);
        Assert.Equal("Approved", policy.Level);
    }

    // ── ExecuteAsync — missing action ──

    [Fact]
    public async Task ExecuteAsync_MissingAction_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("");

        Assert.Contains("Missing 'action'", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>unknown</action>");

        Assert.Contains("Unknown action", result);
    }

    // ── ExecuteAsync — create ──

    [Fact]
    public async Task ExecuteAsync_Create_NewFile_CreatesFile()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>create</action><file>test.txt</file><content>Hello</content>");

        Assert.Contains("Created", result);
        Assert.Contains("Hello", result);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), "Hello"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Create_ExistingFile_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>create</action><file>test.txt</file><content>Hi</content>");

        Assert.Contains("already exists", result);
    }

    [Fact]
    public async Task ExecuteAsync_Create_MissingFile_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>create</action><content>Hi</content>");

        Assert.Contains("Missing 'file'", result);
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithDirectory_CreatesDirectory()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _fileSystem.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>create</action><file>sub/test.txt</file><content>Hi</content>");

        Assert.Contains("Created", result);
        _fileSystem.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
    }

    // ── ExecuteAsync — patch ──

    [Fact]
    public async Task ExecuteAsync_Patch_ValidOldText_AppliesPatch()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Hello World");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>test.txt</file><old_text>Hello</old_text><new_text>Hi</new_text>");

        Assert.Contains("Patched", result);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), "Hi World"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_FileNotFound_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>nofile.txt</file><old_text>a</old_text><new_text>b</new_text>");

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_OldTextNotFound_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Hello World");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>test.txt</file><old_text>Goodbye</old_text><new_text>Hi</new_text>");

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_MultipleOccurrences_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("test test test");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>test.txt</file><old_text>test</old_text><new_text>xyz</new_text>");

        Assert.Contains("found 3 times", result);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_OldTextNotFound_ReturnsSimilarLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Hello World\nGoodbye World");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>test.txt</file><old_text>Hello World Test</old_text><new_text>Hi</new_text>");

        Assert.Contains("not found", result);
        Assert.Contains("Similar", result);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_MissingArguments_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>test.txt</file>");

        Assert.Contains("Missing", result);
    }

    [Fact]
    public async Task ExecuteAsync_Patch_Cancelled_ReturnsCancelledMessage()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Hello World");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>test.txt</file><old_text>Hello</old_text><new_text>Hi</new_text>", cts.Token);

        Assert.Contains("CANCELLED", result);
    }

    // ── ExecuteAsync — diff ──

    [Fact]
    public async Task ExecuteAsync_Diff_ReturnsDiff()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("old line same line");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>diff</action><file>test.txt</file><new_text>new line same line</new_text>");

        Assert.Contains("Diff", result);
        Assert.Contains("new line", result);
    }

    [Fact]
    public async Task ExecuteAsync_Diff_FileNotFound_ReturnsError()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>diff</action><file>nofile.txt</file><new_text>x</new_text>");

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ExecuteAsync_Diff_NoChanges_ReturnsNoChanges()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("same");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>diff</action><file>test.txt</file><new_text>same</new_text>");

        Assert.Contains("no changes", result);
    }

    // ── ExecuteAsync — search ──

    [Fact]
    public async Task ExecuteAsync_Search_MissingPattern_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>search</action>");

        Assert.Contains("Missing 'pattern'", result);
    }

    // ── ExecuteAsync — insert ──

    [Fact]
    public async Task ExecuteAsync_Insert_AtLine_ReturnsInsertedMessage()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline3");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>insert</action><file>test.txt</file><line>2</line><text>line2</text>");

        Assert.Contains("Inserted", result);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), It.Is<string>(s => s.Contains("line2"))), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Insert_MissingArguments_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>insert</action><file>test.txt</file>");

        Assert.Contains("Missing", result);
    }

    [Fact]
    public async Task ExecuteAsync_Insert_InvalidLine_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>insert</action><file>test.txt</file><line>abc</line><text>x</text>");

        Assert.Contains("Missing", result);
    }

    // ── ExecuteAsync — delete-lines ──

    [Fact]
    public async Task ExecuteAsync_DeleteLines_ValidRange_DeletesLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("line1\nline2\nline3\nline4");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>delete-lines</action><file>test.txt</file><start_line>2</start_line><end_line>3</end_line>");

        Assert.Contains("Deleted", result);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), It.Is<string>(s => !s.Contains("line2") && !s.Contains("line3"))), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteLines_InvalidNumbers_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>delete-lines</action><file>test.txt</file><start_line>abc</start_line><end_line>3</end_line>");

        Assert.Contains("Missing", result);
    }

    // ── CountOccurrences (tested indirectly via patch) ──

    [Fact]
    public async Task ExecuteAsync_Patch_SingleOccurrence_AppliesPatch()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("old text here");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>test.txt</file><old_text>old text</old_text><new_text>new text</new_text>");

        Assert.Contains("Patched", result);
        _fileSystem.Verify(f => f.WriteFile(It.IsAny<string>(), "new text here"), Times.Once);
    }

    // ── FindSimilarLines (tested indirectly via patch) ──

    [Fact]
    public async Task ExecuteAsync_Patch_SimilarLinesFound_ReturnsSimilarLines()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("function HelloWorld() {\n  return greet;\n}\nfunction Hello() {\n  return greet;\n}");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>patch</action><file>test.txt</file><old_text>function HelloWorld Test</old_text><new_text>x</new_text>");

        Assert.Contains("not found", result);
    }

    // ── GenerateDiff (tested indirectly via diff action) ──

    [Fact]
    public async Task ExecuteAsync_Diff_WithChanges_ReturnsPlusMinusDiff()
    {
        _fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _fileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("old line same line");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>diff</action><file>test.txt</file><new_text>new line same line</new_text>");

        Assert.Contains("- ", result);
        Assert.Contains("+ ", result);
        Assert.Contains("old line", result);
        Assert.Contains("new line", result);
    }

    // ── Replace-All ──

    [Fact]
    public async Task ExecuteAsync_ReplaceAll_MissingPattern_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("<action>replace-all</action>");

        Assert.Contains("Missing 'pattern'", result);
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

        var result = await tool.ExecuteAsync("<action>insert</action><file>test.txt</file><line>1</line><text>new</text>", cts.Token);

        Assert.Contains("CANCELLED", result);
    }
}