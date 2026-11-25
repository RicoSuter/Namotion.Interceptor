using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Secrets;

public class SecretConverterTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-secret-converter-");

    private string StorePath => Path.Combine(_directory.FullName, "Secrets.json");

    [Fact]
    public void WhenATopLevelSecretIsPlaintext_ThenItIsReplacedByAReferenceAndStored()
    {
        // Arrange
        var (serializer, store) = Create();
        var json = $$"""{ "$type": "{{typeof(ConverterDevice).FullName}}", "host": "10.0.0.1", "password": "s3cr3t" }""";

        // Act
        var isConverted = serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out var converted);

        // Assert
        Assert.True(isConverted);
        var reference = JsonNode.Parse(converted)!["password"]!.GetValue<string>();
        Assert.Matches("^\\$secret:[0-9a-f]{32}$", reference);
        Assert.True(store.TryGet(reference["$secret:".Length..], out var value));
        Assert.Equal("s3cr3t", value);
        Assert.Equal("10.0.0.1", JsonNode.Parse(converted)!["host"]!.GetValue<string>());
    }

    [Fact]
    public void WhenTheNamingIsCSharp_ThenTheDeclaredNameIsMatched()
    {
        // Arrange
        var (serializer, _) = Create();
        var json = """{ "Host": "10.0.0.1", "Password": "s3cr3t" }""";

        // Act
        var isConverted = serializer.TryConvertSecrets(json, typeof(ConverterDevice), ConfigurationNaming.CSharpProperty, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.StartsWith("$secret:", JsonNode.Parse(converted)!["Password"]!.GetValue<string>());
    }

    [Fact]
    public void WhenANestedSubjectWithoutTypeDiscriminatorHasAPlaintextSecret_ThenItIsConverted()
    {
        // Arrange
        var (serializer, _) = Create();
        var json = $$"""{ "$type": "{{typeof(ConverterDevice).FullName}}", "child": { "password": "inner" } }""";

        // Act
        var isConverted = serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.StartsWith("$secret:", JsonNode.Parse(converted)!["child"]!["password"]!.GetValue<string>());
    }

    [Fact]
    public void WhenAListAndADictionaryOfSubjectsHoldPlaintextSecrets_ThenAllAreConverted()
    {
        // Arrange
        var (serializer, store) = Create();
        var json = $$"""
            {
              "$type": "{{typeof(ConverterDevice).FullName}}",
              "children": [ { "password": "one" } ],
              "named": { "a": { "password": "two" } }
            }
            """;

        // Act
        var isConverted = serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out var converted);

        // Assert
        Assert.True(isConverted);
        var root = JsonNode.Parse(converted)!;
        Assert.StartsWith("$secret:", root["children"]![0]!["password"]!.GetValue<string>());
        Assert.StartsWith("$secret:", root["named"]!["a"]!["password"]!.GetValue<string>());
        Assert.Equal(2, JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath))!.Count);
    }

    [Fact]
    public void WhenANestedPropertyHasAJsonPropertyName_ThenThatNameIsMatchedBelowTheTopLevel()
    {
        // Arrange
        var (serializer, _) = Create();
        var json = $$"""{ "$type": "{{typeof(ConverterDevice).FullName}}", "renamed": { "pass_word": "inner" } }""";

        // Act
        var isConverted = serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.StartsWith("$secret:", JsonNode.Parse(converted)!["renamed"]!["pass_word"]!.GetValue<string>());
    }

    [Fact]
    public void WhenANestedObjectHasATypeDiscriminator_ThenItIsConvertedAsThatType()
    {
        // Arrange
        var (serializer, _) = Create();
        var json = $$"""
            { "$type": "{{typeof(ConverterDevice).FullName}}", "any": { "$type": "{{typeof(ConverterDevice).FullName}}", "password": "inner" } }
            """;

        // Act
        var isConverted = serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.StartsWith("$secret:", JsonNode.Parse(converted)!["any"]!["password"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"$secret:0123456789abcdef0123456789abcdef\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void WhenTheSecretIsAReferenceOrEmpty_ThenNothingIsConvertedAndTheTextIsReturnedAsItIs(string value)
    {
        // Arrange
        var (serializer, _) = Create();
        var json = $$"""{ "$type": "{{typeof(ConverterDevice).FullName}}", "password": {{value}} }""";

        // Act
        var isConverted = serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out var converted);

        // Assert
        Assert.False(isConverted);
        Assert.Same(json, converted);
        Assert.False(File.Exists(StorePath));
    }

    [Theory]
    [InlineData("""{ "password": "s3cr3t" }""")]
    [InlineData("""{ "$type": "Unknown.Type", "password": "s3cr3t" }""")]
    [InlineData("""not json""")]
    [InlineData("""[ 1, 2 ]""")]
    public void WhenTheRootTypeIsMissingOrUnknownOrTheTextIsNotAnObject_ThenNothingIsConverted(string json)
    {
        // Arrange
        var (serializer, _) = Create();

        // Act
        var isConverted = serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out var converted);

        // Assert
        Assert.False(isConverted);
        Assert.Same(json, converted);
    }

    [Fact]
    public void WhenNoSecretStoreIsRegistered_ThenNothingIsConverted()
    {
        // Arrange
        var (serializer, _) = Create(withStore: false);
        var json = $$"""{ "$type": "{{typeof(ConverterDevice).FullName}}", "password": "s3cr3t" }""";

        // Act
        var isConverted = serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out var converted);

        // Assert
        Assert.False(isConverted);
        Assert.Same(json, converted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(""", "pin": 1234""")]
    public void WhenATypeHasASecretThatIsNotAString_ThenConvertingThrowsWithOrWithoutAValueForIt(string pin)
    {
        // Arrange
        var (serializer, _) = Create();
        var json = $$"""{ "$type": "{{typeof(ConverterInvalidDevice).FullName}}"{{pin}} }""";

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out _));
    }

    [Fact]
    public void WhenTheStoreCannotBeWritten_ThenConvertingThrowsAndStoresNothing()
    {
        // Arrange
        var (serializer, _) = Create();
        Directory.CreateDirectory(StorePath);
        var json = $$"""{ "$type": "{{typeof(ConverterDevice).FullName}}", "password": "s3cr3t" }""";

        // Act
        var exception = Assert.ThrowsAny<Exception>(() => serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out _));

        // Assert
        Assert.True(exception is IOException or UnauthorizedAccessException, exception.ToString());

        // A leaked entry of the failed write would be persisted along with the next one.
        Directory.Delete(StorePath);
        Assert.True(serializer.TryConvertSecrets(json, ConfigurationNaming.JsonProperty, out _));
        Assert.Single(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath))!);
    }

    private (ConfigurableSubjectSerializer Serializer, SecretStore Store) Create(bool withStore = true)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(ConverterDevice), typeof(ConverterInvalidDevice)]);
        var store = new SecretStore(StorePath);
        var services = new ServiceCollection();
        if (withStore)
        {
            services.AddSingleton(store);
        }

        return (new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider()), store);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}

[InterceptorSubject]
public partial class ConverterDevice : IConfigurable
{
    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [Configuration]
    public partial ConverterDevice? Child { get; set; }

    [Configuration]
    public partial List<ConverterDevice>? Children { get; set; }

    [Configuration]
    public partial Dictionary<string, ConverterDevice>? Named { get; set; }

    [Configuration]
    public partial ConverterRenamedDevice? Renamed { get; set; }

    [Configuration]
    public partial IConfigurable? Any { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class ConverterRenamedDevice : IConfigurable
{
    [Configuration(IsSecret = true)]
    [JsonPropertyName("pass_word")]
    public partial string? Password { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class ConverterInvalidDevice : IConfigurable
{
    [Configuration(IsSecret = true)]
    public partial int Pin { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
