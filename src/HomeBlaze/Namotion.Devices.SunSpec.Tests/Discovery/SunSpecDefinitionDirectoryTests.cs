using Namotion.Devices.SunSpec.Discovery;
using Namotion.Devices.SunSpec.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.SunSpec.Tests.Discovery;

public sealed class SunSpecDefinitionDirectoryTests : IDisposable
{
    private const string DuplicateNameJson = """
        { "id": 64997, "group": { "name": "vendor", "points": [
            { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 },
            { "name": "W", "type": "int16", "size": 1 }, { "name": "W", "type": "int16", "size": 1 } ] } }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sunspec-definitions-{Guid.NewGuid():N}");

    public SunSpecDefinitionDirectoryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void WhenFolderHasAValidDefinition_ThenItIsLoaded()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_directory, "model_64999.json"), SunSpecDynamicModelTests.DefinitionJson);

        // Act
        var definitions = new SunSpecDefinitionDirectory().Load(_directory, _ => false, new RecordingLogger());

        // Assert
        Assert.Equal(64999, Assert.Single(definitions).Key);
    }

    [Fact]
    public void WhenADefinitionIsBuiltIn_ThenItIsIgnoredWithAWarning()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_directory, "model_64999.json"), SunSpecDynamicModelTests.DefinitionJson);
        var logger = new RecordingLogger();

        // Act
        var definitions = new SunSpecDefinitionDirectory().Load(_directory, modelId => modelId == 64999, logger);

        // Assert
        Assert.Empty(definitions);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void WhenTwoFilesDefineTheSameModel_ThenTheFirstIsUsedAndTheSecondIsIgnoredWithAWarning()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_directory, "a.json"), SunSpecDynamicModelTests.DefinitionJson);
        File.WriteAllText(Path.Combine(_directory, "b.json"), SunSpecDynamicModelTests.DefinitionJson.Replace("Vendor Block", "Other", StringComparison.Ordinal));
        var logger = new RecordingLogger();

        // Act
        var definitions = new SunSpecDefinitionDirectory().Load(_directory, _ => false, logger);

        // Assert
        Assert.Equal("Vendor Block", Assert.Single(definitions).Value.Group.Label);
        Assert.Contains("b.json", Assert.Single(logger.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData(DuplicateNameJson)]
    public void WhenADefinitionIsInvalid_ThenItIsIgnoredWithAWarning(string json)
    {
        // Arrange
        File.WriteAllText(Path.Combine(_directory, "broken.json"), json);
        var logger = new RecordingLogger();

        // Act
        var definitions = new SunSpecDefinitionDirectory().Load(_directory, _ => false, logger);

        // Assert
        Assert.Empty(definitions);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void WhenTheFolderDoesNotExist_ThenNoDefinitionsAreLoaded()
    {
        // Act
        var definitions = new SunSpecDefinitionDirectory().Load(Path.Combine(_directory, "missing"), _ => false, new RecordingLogger());

        // Assert
        Assert.Empty(definitions);
    }

    [Fact]
    public void WhenAFileIsUnchanged_ThenTheCatalogOfTheNextLoadKeepsItsDynamicModel()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_directory, "model_64999.json"), SunSpecDynamicModelTests.DefinitionJson);
        var directory = new SunSpecDefinitionDirectory();
        var first = directory.Load(_directory, _ => false, new RecordingLogger());
        var entry = new SunSpecChainEntry(64999, 40100, 4, [64999, 4, 1500, 0, 0, 0]);
        var model = new SunSpecModelCatalog(first).Create(entry);

        // Act
        var second = directory.Load(_directory, _ => false, new RecordingLogger());

        // Assert
        Assert.Same(first[64999], second[64999]);
        Assert.True(new SunSpecModelCatalog(second).IsCurrent(model, entry));
    }

    [Fact]
    public void WhenAFileChanges_ThenItsDefinitionIsParsedAgain()
    {
        // Arrange
        var file = Path.Combine(_directory, "model_64999.json");
        File.WriteAllText(file, SunSpecDynamicModelTests.DefinitionJson);
        var directory = new SunSpecDefinitionDirectory();
        var first = directory.Load(_directory, _ => false, new RecordingLogger());

        // Act
        File.WriteAllText(file, SunSpecDynamicModelTests.DefinitionJson.Replace("Vendor Block", "Changed", StringComparison.Ordinal));
        var second = directory.Load(_directory, _ => false, new RecordingLogger());

        // Assert
        Assert.NotSame(first[64999], second[64999]);
        Assert.Equal("Changed", second[64999].Group.Label);
    }
}
