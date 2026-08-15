using ECAssistant.Interfaces;
using ECAssistant.Tools;
using ECAssistant.Tools.Shell;
using ECAssistant.Tools.Build;
using ECAssistant.Tools.Code;
using ECAssistant.Tools.Reader;
using ECAssistant.Tools.Web;
using ECAssistant.Tools.Research;
using ECAssistant.Tools.Git;
using Moq;

namespace ECAssistant.Tests.Integration;

/// <summary>
/// Tests that verify the full ToolAdapter round-trip:
/// Dict args → ToolAdapter.ArgumentsToXml → tool.ExecuteAsync → tool.ParseInput → correct values.
/// This catches format mismatches between what ToolAdapter produces and what tools expect.
/// </summary>
[Collection("ProgramGui")]
public class ToolAdapterRoundTripTests
{
    private static string TempDir => Path.GetTempPath();

    private static Mock<IConfigProvider> MockConfig()
    {
        var config = new Mock<IConfigProvider>();
        config.Setup(c => c.GetValue(It.Is<string>(k => k == "workingDir"), It.IsAny<string>()))
              .Returns(TempDir);
        config.Setup(c => c.GetValue(It.Is<string>(k => k == "maxOutputChars"), It.IsAny<string>()))
              .Returns("50000");
        return config;
    }

    private static Mock<IProcessRunner> MockProcessRunner(string stdout = "ok", int exitCode = 0)
    {
        var mock = new Mock<IProcessRunner>();
        mock.Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(exitCode, stdout, "", false));
        return mock;
    }

    [Fact]
    public async Task EShellAgent_AdapterRoundTrip_CommandExtractedCorrectly()
    {
        var runner = MockProcessRunner("Sat Aug 15 10:00:00 CEST 2026");
        var tool = new EShellAgent(runner.Object, MockConfig().Object, new Mock<IColorFormatter>().Object, TempDir);
        var adapter = new ToolAdapter(tool);

        var args = new Dictionary<string, string?> { ["command"] = "date" };
        var result = await adapter.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Succeeded);
        runner.Verify(p => p.ExecuteAsync(
            It.Is<string>(cmd => cmd.Contains("/bin/zsh -c \"date\"")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), "Shell command should contain the extracted 'date' command, not 'command=\"date\"'");
    }

    [Fact]
    public async Task EShellAgent_AdapterRoundTrip_ComplexCommand()
    {
        var runner = MockProcessRunner("hello");
        var tool = new EShellAgent(runner.Object, MockConfig().Object, new Mock<IColorFormatter>().Object, TempDir);
        var adapter = new ToolAdapter(tool);

        var args = new Dictionary<string, string?> { ["command"] = "echo hello" };
        var result = await adapter.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Succeeded);
        runner.Verify(p => p.ExecuteAsync(
            It.Is<string>(cmd => cmd.Contains("echo hello")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task EDotnetBuildTool_AdapterRoundTrip_ActionExtractedCorrectly()
    {
        var runner = MockProcessRunner("Build succeeded.", 0);
        var tool = new EDotnetBuildTool(runner.Object, MockConfig().Object, new Mock<IColorFormatter>().Object);
        var adapter = new ToolAdapter(tool);

        var args = new Dictionary<string, string?> { ["action"] = "build", ["projectPath"] = "MyApp.csproj" };
        var result = await adapter.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Succeeded);
        runner.Verify(p => p.ExecuteAsync(
            It.Is<string>(cmd => cmd.Contains("dotnet build") && cmd.Contains("MyApp.csproj")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task EFileReaderTool_AdapterRoundTrip_FilePathExtractedCorrectly()
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        fs.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("file content");

        var tool = new EFileReaderTool(fs.Object, MockConfig().Object, new Mock<IColorFormatter>().Object);
        var adapter = new ToolAdapter(tool);

        var args = new Dictionary<string, string?> { ["file"] = "test.txt" };
        var result = await adapter.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Succeeded);
        fs.Verify(f => f.ReadFile(It.Is<string>(p => p.Contains("test.txt"))));
    }

    [Fact]
    public async Task EWebFetchTool_AdapterRoundTrip_UrlExtractedCorrectly()
    {
        var http = new Mock<IHttpClient>();
        http.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body>Hello</body></html>");

        var tool = new EWebFetchTool(http.Object, MockConfig().Object, new Mock<IColorFormatter>().Object);
        var adapter = new ToolAdapter(tool);

        var args = new Dictionary<string, string?> { ["url"] = "https://example.com" };
        var result = await adapter.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Succeeded);
        http.Verify(h => h.GetAsync(
            It.Is<string>(url => url == "https://example.com"),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task EWebSearchTool_AdapterRoundTrip_QueryExtractedCorrectly()
    {
        var http = new Mock<IHttpClient>();
        http.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html>results</html>");

        var tool = new EWebSearchTool(http.Object, MockConfig().Object, new Mock<IColorFormatter>().Object);
        var adapter = new ToolAdapter(tool);

        var args = new Dictionary<string, string?> { ["query"] = "C# async tips" };
        await adapter.ExecuteAsync(args, CancellationToken.None);

        // Verify the query was extracted and used in the API URL
        http.Verify(h => h.GetAsync(
            It.Is<string>(url => url.Contains("async") || url.Contains("C%23")),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ECodeEditorTool_AdapterRoundTrip_CreateActionExtractedCorrectly()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "ecatest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fs = new Mock<IFileSystem>();
            fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
            fs.Setup(f => f.WriteFile(It.IsAny<string>(), It.IsAny<string>()));

            var config = new Mock<IConfigProvider>();
            config.Setup(c => c.GetValue(It.Is<string>(k => k == "workingDir"), It.IsAny<string>()))
                  .Returns(tmpDir);

            var tool = new ECodeEditorTool(fs.Object, config.Object, new Mock<IColorFormatter>().Object);
            var adapter = new ToolAdapter(tool);

            var args = new Dictionary<string, string?>
            {
                ["action"] = "create",
                ["file"] = "newfile.txt",
                ["content"] = "hello world"
            };
            var result = await adapter.ExecuteAsync(args, CancellationToken.None);

            Assert.True(result.Succeeded);
            fs.Verify(f => f.WriteFile(
                It.Is<string>(p => p.Contains("newfile.txt")),
                It.Is<string>(c => c.Contains("hello world"))));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task EGitTool_AdapterRoundTrip_ActionExtractedCorrectly()
    {
        var runner = MockProcessRunner("On branch main");
        var fs = new Mock<IFileSystem>();

        var tool = new EGitTool(runner.Object, fs.Object, MockConfig().Object, new Mock<IColorFormatter>().Object);
        var adapter = new ToolAdapter(tool);

        var args = new Dictionary<string, string?> { ["action"] = "status" };
        var result = await adapter.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Succeeded);
        runner.Verify(p => p.ExecuteAsync(
            It.Is<string>(cmd => cmd.Contains("git status")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void ToolAdapter_EmptyArgs_ProducesEmptyString()
    {
        var tool = new Mock<ITool>();
        tool.SetupGet(t => t.Name).Returns("TestTool");
        tool.SetupGet(t => t.Description).Returns("Test");
        tool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");
        tool.Setup(t => t.GetPolicy()).Returns(ECAssistant.Interfaces.ToolPolicy.Allowed("TestTool"));

        var adapter = new ToolAdapter(tool.Object);
        var args = new Dictionary<string, string?>();
        adapter.ExecuteAsync(args, CancellationToken.None);

        tool.Verify(t => t.ExecuteAsync("", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void ToolAdapter_NullArgs_ProducesEmptyString()
    {
        var tool = new Mock<ITool>();
        tool.SetupGet(t => t.Name).Returns("TestTool");
        tool.SetupGet(t => t.Description).Returns("Test");
        tool.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");
        tool.Setup(t => t.GetPolicy()).Returns(ECAssistant.Interfaces.ToolPolicy.Allowed("TestTool"));

        var adapter = new ToolAdapter(tool.Object);
        adapter.ExecuteAsync(null!, CancellationToken.None);

        tool.Verify(t => t.ExecuteAsync("", It.IsAny<CancellationToken>()));
    }
}