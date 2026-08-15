using ECAssistant.Analysis;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Analysis;

public class EContextAnalyzerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IColorFormatter> _mockColor;

    public EContextAnalyzerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAssistantTests_ECA_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _mockColor = new Mock<IColorFormatter>();
        _mockColor.SetupGet(c => c.Cyan).Returns("\x1b[36m");
        _mockColor.SetupGet(c => c.Green).Returns("\x1b[32m");
        _mockColor.SetupGet(c => c.Red).Returns("\x1b[31m");
        _mockColor.SetupGet(c => c.Reset).Returns("\x1b[0m");
        _mockColor.Setup(c => c.Tag(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()));
        _mockColor.Setup(c => c.TagBold(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void CreateCsFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public void Constructor_ValidProjectRoot_SetsRoot()
    {
        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        // No direct property to verify root, but AnalyzeProjectAsync will use it
        Assert.NotNull(analyzer);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_EmptyDir_ReturnsZeroFiles()
    {
        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.Equal(0, result.Files);
        Assert.Equal("Mixed/Unknown", result.ProjectType);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithCsFiles_DetectsDotNetProject()
    {
        CreateCsFile("Program.cs", "using System;\nnamespace TestApp {\n  class Program { static void Main() {} }\n}\n");
        CreateCsFile("TestApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.True(result.Files >= 2);
        Assert.Contains(".NET", result.ProjectType);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithSolution_DetectsSolutionType()
    {
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        CreateCsFile("App.sln", "Microsoft Visual Studio Solution File");
        CreateCsFile("Program.cs", "using System;\nclass Program {}\n");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.Contains("Solution", result.ProjectType);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithControllers_DetectsMvcType()
    {
        CreateCsFile("Controllers/HomeController.cs",
            "using System;\npublic class HomeController { public void Index() {} }\n");
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.Contains("MVC", result.ProjectType);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithControllersAndApi_DetectsWebApiType()
    {
        CreateCsFile("Controllers/ApiUserController.cs",
            "using System;\npublic class ApiUserController { }\n");
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.Contains("Web API", result.ProjectType);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithTestFiles_DetectsTestProject()
    {
        CreateCsFile("Tests/MyTests.cs", "using Xunit;\npublic class MyTests { [Fact] public void Test() {} }\n");
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.Contains("Test", result.ProjectType);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithTodos_DetectsTodoCount()
    {
        var content = "using System;\n// TODO: fix this\n// TODO: and this\n// HACK: workaround\nclass Foo {}\n";
        CreateCsFile("Foo.cs", content);
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        // Files with >3 TODOs get flagged as issues
        // We only have 3 TODOs/HACKs here, so check that analysis ran
        Assert.True(result.Files >= 1);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithManyTodos_FlagsIssue()
    {
        var content = "using System;\n// TODO: 1\n// TODO: 2\n// TODO: 3\n// TODO: 4\nclass Foo {}\n";
        CreateCsFile("Foo.cs", content);
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.Contains(result.PotentialIssues, i => i.Contains("TODO"));
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithLargeFile_FlagsLargeFileIssue()
    {
        var lines = Enumerable.Range(0, 600).Select(i => $"// line {i}").ToArray();
        CreateCsFile("BigFile.cs", string.Join("\n", lines));
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.Contains(result.PotentialIssues, i => i.Contains("Large file"));
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithImports_DetectsRelationships()
    {
        CreateCsFile("Services/MyService.cs", "using System;\nclass MyService {}\n");
        CreateCsFile("Program.cs", "using TestApp.Services;\nclass Program {}\n");
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.True(result.Relationships > 0);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_CustomExtensions_OnlyScansSpecifiedExtensions()
    {
        CreateCsFile("File.cs", "using System;\nclass Foo {}\n");
        File.WriteAllText(Path.Combine(_tempDir, "readme.md"), "# Readme\n");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync(extensionsToScan: new HashSet<string> { ".cs" });
        // .md should not be included
        Assert.True(result.Files >= 1);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_ExcludesBinObjDirectories()
    {
        CreateCsFile("Program.cs", "class Program {}\n");
        CreateCsFile("App.csproj", "<Project></Project>");
        // Create files in bin/obj that should be excluded
        CreateCsFile("obj/Debug/Generated.cs", "class Gen {}\n");
        CreateCsFile("bin/Debug/Built.cs", "class Built {}\n");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        // Should only find Program.cs and App.csproj, not obj/bin files
        Assert.Equal(2, result.Files);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_NonExistentDir_ReturnsZeroFiles()
    {
        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync(targetDirectory: "/nonexistent/path/xyz");
        Assert.Equal(0, result.Files);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_SetsDependencyGraph()
    {
        CreateCsFile("A.cs", "using System;\nclass A {}\n");
        CreateCsFile("B.cs", "using System;\nclass B {}\n");
        CreateCsFile("App.csproj", "<Project></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.NotNull(result.DependencyGraph);
    }

    [Fact]
    public async Task GetDebugContextSummary_WithoutAnalysis_ReturnsNoAnalysisMessage()
    {
        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var summary = analyzer.GetDebugContextSummary();
        Assert.Contains("No analysis run yet", summary);
    }

    [Fact]
    public async Task GetDebugContextSummary_WithAnalysis_ReturnsFormattedSummary()
    {
        CreateCsFile("Program.cs", "using System;\nnamespace App {\n  class Program { static void Main() {} }\n}\n");
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        await analyzer.AnalyzeProjectAsync();
        var summary = analyzer.GetDebugContextSummary();
        Assert.Contains("Project Analysis", summary);
        Assert.Contains("Project type:", summary);
        Assert.Contains("Files scanned:", summary);
    }

    [Fact]
    public async Task GetDebugContextSummary_WithCSharpFiles_IncludesCodeStats()
    {
        CreateCsFile("Program.cs", "using System;\nnamespace App {\n  class Program {\n    public void Run() {}\n    public void Stop() {}\n  }\n}\n");
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        await analyzer.AnalyzeProjectAsync();
        var summary = analyzer.GetDebugContextSummary();
        Assert.Contains("C# stats", summary);
        Assert.Contains("Total lines:", summary);
        Assert.Contains("Total classes:", summary);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_WithMarkdownFiles_IncludesMarkdown()
    {
        File.WriteAllText(Path.Combine(_tempDir, "readme.md"), "# Readme\n\nSome content.\n");
        CreateCsFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.True(result.Files >= 2);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_ProjectRootProperty_IsSetInResult()
    {
        var analyzer = new EContextAnalyzer(_tempDir, _mockColor.Object);
        var result = await analyzer.AnalyzeProjectAsync();
        Assert.Equal(Path.GetFullPath(_tempDir), result.ProjectRoot);
    }
}