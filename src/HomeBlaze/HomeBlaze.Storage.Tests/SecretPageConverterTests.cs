using HomeBlaze.Services;
using HomeBlaze.Services.Secrets;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Storage.Tests;

public class SecretPageConverterTests : IDisposable
{
    private static readonly string DeviceType = typeof(EmbeddedSecretDevice).FullName!;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-page-converter-");

    [Fact]
    public void WhenAHeadArgumentHoldsAPlaintextSecret_ThenOnlyItIsReplaced()
    {
        // Arrange
        var converter = Create();
        var source = $"# Gateway\n\nIntro.\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n\nOutro.\n";

        // Act
        var isConverted = converter.TryConvert(source, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.StartsWith($"# Gateway\n\nIntro.\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=$secret:", converted);
        Assert.EndsWith("\n::\n\nOutro.\n", converted);
        Assert.DoesNotContain("s3cr3t", converted);
    }

    [Fact]
    public void WhenAFenceHoldsAPlaintextSecret_ThenTheFenceIsRewrittenWithAReference()
    {
        // Arrange
        var converter = Create();
        var source = $"# Gateway\n\n:: {DeviceType}#gateway\n```json\n{{\n  \"Host\": \"10.0.0.1\",\n  \"Password\": \"s3cr3t\"\n}}\n```\n::\n";

        // Act
        var isConverted = converter.TryConvert(source, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.StartsWith($"# Gateway\n\n:: {DeviceType}#gateway\n```json\n", converted);
        Assert.Contains("\"Host\": \"10.0.0.1\"", converted);
        Assert.Contains("\"Password\": \"$secret:", converted);
        Assert.EndsWith("\n```\n::\n", converted);
    }

    [Fact]
    public void WhenTheLineEndingIsCrLf_ThenTheRewrittenFenceKeepsIt()
    {
        // Arrange
        var converter = Create();
        var source = $"# Gateway\r\n\r\n:: {DeviceType}#gateway\r\n```json\r\n{{ \"Password\": \"s3cr3t\" }}\r\n```\r\n::\r\n";

        // Act
        converter.TryConvert(source, out var converted);

        // Assert
        Assert.DoesNotContain("s3cr3t", converted);
        Assert.DoesNotContain("\n", converted.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void WhenASlotValueBlockHoldsAPlaintextSecret_ThenItIsConvertedWithTheSlotType()
    {
        // Arrange
        var converter = Create();
        var source = $":: {typeof(SecretSlotHost).FullName}#host\n::: Gateway Password=s3cr3t\n:::\n::\n";

        // Act
        var isConverted = converter.TryConvert(source, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Contains("::: Gateway Password=$secret:", converted);
    }

    [Fact]
    public void WhenAWidgetIsNestedInAContainerBlock_ThenItIsConverted()
    {
        // Arrange
        var converter = Create();
        var source = $":: HomeBlaze.Components.Card#card Title=Note\n::: {DeviceType}#inner Password=s3cr3t\n:::\n::\n";

        // Act
        var isConverted = converter.TryConvert(source, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Contains($"::: {DeviceType}#inner Password=$secret:", converted);
        Assert.StartsWith(":: HomeBlaze.Components.Card#card Title=Note\n", converted);
    }

    [Fact]
    public void WhenSeveralBlocksHoldPlaintextSecrets_ThenAllAreConvertedAndOtherTextIsUnchanged()
    {
        // Arrange
        var converter = Create();
        var source = $"A\n\n:: {DeviceType}#a Password=one\n::\n\nB\n\n:: {DeviceType}#b Password=two\n::\n\nC\n";

        // Act
        converter.TryConvert(source, out var converted);

        // Assert
        Assert.DoesNotContain("Password=one", converted);
        Assert.DoesNotContain("Password=two", converted);
        Assert.StartsWith("A\n\n", converted);
        Assert.EndsWith("::\n\nC\n", converted);
    }

    [Theory]
    [InlineData(":: {0}#gateway Host=10.0.0.1\n::\n")]
    [InlineData(":: {0}#gateway Password=$secret:0123456789abcdef0123456789abcdef\n::\n")]
    [InlineData(":: Unknown.Type#x Password=s3cr3t\n::\n")]
    [InlineData("# Just text\n")]
    public void WhenNothingNeedsConverting_ThenTheSourceIsReturnedAsItIs(string format)
    {
        // Arrange
        var converter = Create();
        var source = string.Format(format, DeviceType);

        // Act
        var isConverted = converter.TryConvert(source, out var converted);

        // Assert
        Assert.False(isConverted);
        Assert.Same(source, converted);
    }

    [Fact]
    public void WhenAFenceHasDuplicateKeys_ThenItIsSkipped()
    {
        // Arrange
        var converter = Create();
        var source = $":: {DeviceType}#gateway\n```json\n{{ \"Password\": \"a\", \"Password\": \"b\" }}\n```\n::\n";

        // Act
        var isConverted = converter.TryConvert(source, out var converted);

        // Assert
        Assert.False(isConverted);
        Assert.Same(source, converted);
    }

    [Fact]
    public void WhenThePageIsNestedBeyondTheDepthLimit_ThenNothingIsConverted()
    {
        // Arrange
        var converter = Create();
        var source = $":: {DeviceType}#gateway Password=s3cr3t\n::\n\n" + new string('>', 4096);

        // Act
        var isConverted = converter.TryConvert(source, out var converted);

        // Assert
        Assert.False(isConverted);
        Assert.Same(source, converted);
    }

    private SecretPageConverter Create()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(EmbeddedSecretDevice), typeof(SecretSlotHost), typeof(HomeBlaze.Components.Card)]);
        var store = new SecretStore(Path.Combine(_directory.FullName, "Secrets.json"));
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        return new SecretPageConverter(new ConfigurableSubjectSerializer(typeProvider, services), typeProvider);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
