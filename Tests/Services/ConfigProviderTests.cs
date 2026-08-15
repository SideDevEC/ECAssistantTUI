using ECAssistant.Services;
using ECAssistant.Interfaces;
using Moq;

namespace ECAssistant.Tests.Services;

public class ConfigProviderTests
{
    private readonly Mock<IFileSystem> _mockFileSystem;
    private const string ConfigPath = "config.json";

    public ConfigProviderTests()
    {
        _mockFileSystem = new Mock<IFileSystem>();
    }

    private void SetupConfigJson(string json)
    {
        _mockFileSystem.Setup(fs => fs.ReadFile(ConfigPath)).Returns(json);
    }

    [Fact]
    public void Constructor_NullFileSystem_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigProvider(null!, ConfigPath));
    }

    [Fact]
    public void Constructor_NullConfigPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigProvider(_mockFileSystem.Object, null!));
    }

    [Fact]
    public void Constructor_ValidArguments_CreatesInstance()
    {
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.NotNull(provider);
    }

    [Fact]
    public void GetValue_ExistingKey_ReturnsValue()
    {
        SetupConfigJson("""{"name":"testvalue"}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.Equal("testvalue", provider.GetValue("name"));
    }

    [Fact]
    public void GetValue_NonExistentKey_ReturnsDefaultValue()
    {
        SetupConfigJson("""{"name":"testvalue"}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.Equal("default", provider.GetValue("missing", "default"));
    }

    [Fact]
    public void GetValue_NullValueInJson_ReturnsDefaultValue()
    {
        SetupConfigJson("""{"name":null}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.Equal("fallback", provider.GetValue("name", "fallback"));
    }

    [Fact]
    public void GetInt_ExistingKey_ReturnsIntValue()
    {
        SetupConfigJson("""{"port":8080}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.Equal(8080, provider.GetInt("port"));
    }

    [Fact]
    public void GetInt_NonExistentKey_ReturnsDefaultValue()
    {
        SetupConfigJson("""{"name":"test"}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.Equal(42, provider.GetInt("missing", 42));
    }

    [Fact]
    public void GetFloat_ExistingKey_ReturnsFloatValue()
    {
        SetupConfigJson("""{"ratio":3.14}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        var result = provider.GetFloat("ratio");
        Assert.InRange(result, 3.13f, 3.15f);
    }

    [Fact]
    public void GetFloat_NonExistentKey_ReturnsDefaultValue()
    {
        SetupConfigJson("""{"name":"test"}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.Equal(1.5f, provider.GetFloat("missing", 1.5f));
    }

    [Fact]
    public void GetBool_ExistingKey_ReturnsBoolValue()
    {
        SetupConfigJson("""{"enabled":true}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.True(provider.GetBool("enabled"));
    }

    [Fact]
    public void GetBool_NonExistentKey_ReturnsDefaultValue()
    {
        SetupConfigJson("""{"name":"test"}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.True(provider.GetBool("missing", true));
    }

    [Fact]
    public void GetBool_FalseValue_ReturnsFalse()
    {
        SetupConfigJson("""{"enabled":false}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.False(provider.GetBool("enabled"));
    }

    [Fact]
    public void GetSection_ExistingSection_ReturnsDeserializedObject()
    {
        SetupConfigJson("""{"llm":{"model":"gpt-4","temperature":0.7}}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        var section = provider.GetSection<TestLlmConfig>("llm");
        Assert.Equal("gpt-4", section.Model);
        Assert.Equal(0.7f, section.Temperature);
    }

    [Fact]
    public void GetSection_NonExistentSection_ThrowsKeyNotFoundException()
    {
        SetupConfigJson("""{"name":"test"}""");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.Throws<KeyNotFoundException>(() => provider.GetSection<TestLlmConfig>("missing"));
    }

    [Fact]
    public void GetValue_EmptyJson_ReturnsDefaultValue()
    {
        SetupConfigJson("{}");
        var provider = new ConfigProvider(_mockFileSystem.Object, ConfigPath);
        Assert.Equal("def", provider.GetValue("any", "def"));
    }

    // Helper class for GetSection tests
    private class TestLlmConfig
    {
        public string Model { get; set; } = "";
        public float Temperature { get; set; }
    }
}