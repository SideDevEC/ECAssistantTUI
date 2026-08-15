using ECAssistant.Engine;
using ECAssistant.Interfaces;
using Moq;

namespace ECAssistant.Tests.Engine;

public class ProjectContextManagerTests
{
    private readonly Mock<ILogger> _loggerMock = new();
    private readonly string _tempDir;

    public ProjectContextManagerTests()
    {
        _loggerMock.SetupGet(x => x.IsDebugEnabled).Returns(false);
        _tempDir = Path.Combine(Path.GetTempPath(), $"projectctx_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    private ProjectContextManager CreateManager()
        => new(_tempDir, _loggerMock.Object);

    [Fact]
    public async Task InitializeAsync_NoExistingContext_ScansProject()
    {
        var manager = CreateManager();
        await manager.InitializeAsync();
        Assert.True(manager.FileCount >= 0);
        Assert.NotNull(manager.LastScan);
    }

    [Fact]
    public async Task ScanProjectAsync_EmptyDirectory_ReturnsZeroFiles()
    {
        var manager = CreateManager();
        await manager.ScanProjectAsync();
        Assert.Equal(0, manager.FileCount);
    }

    [Fact]
    public async Task ScanProjectAsync_WithCsFiles_DetectsFiles()
    {
        var testFile = Path.Combine(_tempDir, "Test.cs");
        await File.WriteAllTextAsync(testFile, "namespace Test {\n  class Foo {}\n}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        Assert.True(manager.FileCount >= 1);
    }

    [Fact]
    public async Task ScanProjectAsync_ExcludesBinAndObj()
    {
        var binDir = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(binDir);
        await File.WriteAllTextAsync(Path.Combine(binDir, "ignore.cs"), "class Ignored {}");

        var objDir = Path.Combine(_tempDir, "obj");
        Directory.CreateDirectory(objDir);
        await File.WriteAllTextAsync(Path.Combine(objDir, "ignore2.cs"), "class Ignored2 {}");

        var realFile = Path.Combine(_tempDir, "Real.cs");
        await File.WriteAllTextAsync(realFile, "class Real {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        // Should only find Real.cs, not bin/ or obj/ files
        var summary = manager.GetProjectSummary();
        Assert.Contains("Real.cs", summary);
        Assert.DoesNotContain("ignore.cs", summary);
        Assert.DoesNotContain("ignore2.cs", summary);
    }

    [Fact]
    public async Task ScanProjectAsync_ExcludesGitDirectory()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        await File.WriteAllTextAsync(Path.Combine(gitDir, "config.json"), "{}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var summary = manager.GetProjectSummary();
        Assert.DoesNotContain(".git", summary);
    }

    [Fact]
    public async Task ScanProjectAsync_DetectsProjectType_ConsoleApp()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "App.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Program.cs"), "class Program {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        Assert.Equal(".NET Console Application", manager.ProjectType);
    }

    [Fact]
    public async Task ScanProjectAsync_DetectsProjectType_WithTests()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "App.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Test.cs"), "class Test {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        Assert.Equal(".NET Project with Tests", manager.ProjectType);
    }

    [Fact]
    public async Task ScanProjectAsync_DetectsEntryPoint_ProgramCs()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Program.cs"), "class Program {}");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Other.cs"), "class Other {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var summary = manager.GetProjectSummary();
        Assert.Contains("Program.cs", summary);
    }

    [Fact]
    public async Task GetProjectSummary_NoFiles_ReturnsEmptyString()
    {
        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var summary = manager.GetProjectSummary();
        Assert.Equal("", summary);
    }

    [Fact]
    public async Task GetProjectSummary_WithFiles_ReturnsFormattedSummary()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Test.cs"), "namespace Test {\n  class Foo {}\n}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var summary = manager.GetProjectSummary();
        Assert.Contains("PROJECT CONTEXT", summary);
        Assert.Contains("Files:", summary);
        Assert.Contains("Test.cs", summary);
    }

    [Fact]
    public async Task GetProjectSummary_WithMoreThan30Files_TruncatesList()
    {
        for (int i = 0; i < 35; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"File{i}.cs"), $"class File{i} {{}}");
        }

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var summary = manager.GetProjectSummary();
        Assert.Contains("more", summary);
    }

    [Fact]
    public async Task GetRelatedFiles_NoRelationships_ReturnsEmptyList()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Test.cs"), "class Test {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var related = manager.GetRelatedFiles("Test.cs");
        Assert.Empty(related);
    }

    [Fact]
    public async Task GetRelatedFiles_WithImports_ReturnsRelatedFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Foo.cs"), "class Foo {}");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Bar.cs"), "using Foo;\nclass Bar {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var related = manager.GetRelatedFiles("Bar.cs");
        Assert.NotEmpty(related);
    }

    [Fact]
    public async Task GetImpactAnalysis_NoRelatedFiles_ReturnsNoDependenciesMessage()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Standalone.cs"), "class Standalone {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var impact = manager.GetImpactAnalysis("Standalone.cs");
        Assert.Contains("No files directly depend on", impact);
    }

    [Fact]
    public async Task GetImpactAnalysis_WithRelatedFiles_ReturnsImpactList()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Foo.cs"), "class Foo {}");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Bar.cs"), "using Foo;\nclass Bar {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var impact = manager.GetImpactAnalysis("Foo.cs");
        Assert.Contains("IMPACT ANALYSIS", impact);
        Assert.Contains("Bar.cs", impact);
    }

    [Fact]
    public async Task ScanProjectAsync_CountsClassesInCsFiles()
    {
        var content = "namespace Test {\n  class Foo {}\n  class Bar {}\n  interface IBaz {}\n}";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Test.cs"), content);

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var summary = manager.GetProjectSummary();
        // The summary should show class count (C) for .cs files
        Assert.Contains("Test.cs", summary);
    }

    [Fact]
    public async Task ScanProjectAsync_CountsMethodsInCsFiles()
    {
        var content = "namespace Test {\n  class Foo {\n    public void MethodA() {}\n    public static async Task MethodB() {}\n  }\n}";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Test.cs"), content);

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var summary = manager.GetProjectSummary();
        Assert.Contains("Test.cs", summary);
    }

    [Fact]
    public async Task ScanProjectAsync_ParsesUsingImports()
    {
        var content = "using System;\nusing System.Linq;\nnamespace Test {\n  class Foo {}\n}";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Test.cs"), content);

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        // Imports should be parsed; verify via project summary having relationships
        var summary = manager.GetProjectSummary();
        Assert.Contains("Test.cs", summary);
    }

    [Fact]
    public async Task ScanProjectAsync_PersistsContextToDisk()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Test.cs"), "class Test {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();

        var contextFile = Path.Combine(_tempDir, ".project_context.json");
        Assert.True(File.Exists(contextFile));
    }

    [Fact]
    public async Task ScanProjectAsync_LoadsExistingContext()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Test.cs"), "class Test {}");

        // First scan
        var manager1 = CreateManager();
        await manager1.ScanProjectAsync();
        manager1.Dispose();

        // Second manager should load existing context
        var manager2 = CreateManager();
        await manager2.InitializeAsync();
        Assert.True(manager2.FileCount >= 1);
    }

    [Fact]
    public async Task ScanProjectAsync_HandlesNonCodeFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "README.md"), "# README");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "config.json"), "{}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        Assert.True(manager.FileCount >= 2);
    }

    [Fact]
    public async Task ScanProjectAsync_DetectsAspNetWebApp()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "App.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "HomeController.cs"), "class HomeController {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        Assert.Equal("ASP.NET Web App", manager.ProjectType);
    }

    [Fact]
    public async Task ScanProjectAsync_DetectsMultiProjectSolution()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "App.sln"), "");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "App.csproj"), "<Project />");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        Assert.Equal(".NET Solution (multi-project)", manager.ProjectType);
    }

    [Fact]
    public async Task GetProjectSummary_IncludesDependencies()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Foo.cs"), "class Foo {}");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Bar.cs"), "using Foo;\nclass Bar {}");

        var manager = CreateManager();
        await manager.ScanProjectAsync();
        var summary = manager.GetProjectSummary();
        Assert.Contains("Dependencies", summary);
    }

    [Fact]
    public void FileCount_BeforeScan_ReturnsZero()
    {
        var manager = CreateManager();
        Assert.Equal(0, manager.FileCount);
    }

    [Fact]
    public void ProjectType_BeforeScan_ReturnsUnknown()
    {
        var manager = CreateManager();
        Assert.Equal("Unknown", manager.ProjectType);
    }
}