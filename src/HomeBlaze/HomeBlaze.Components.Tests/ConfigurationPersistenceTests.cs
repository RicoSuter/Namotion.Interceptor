using HomeBlaze.Components.Tests.Models;
using Xunit;

namespace HomeBlaze.Components.Tests;

public sealed class ConfigurationPersistenceTests : IDisposable
{
    private readonly PersistenceTestGraph _graph = new();

    [Fact]
    public async Task WhenOwnerIsWritten_ThenWrittenIsReturnedAndOwnerIsApplied()
    {
        // Act
        var result = await ConfigurationPersistence.PersistOwnerAsync(_graph.RootManager, _graph.Device, CancellationToken.None);

        // Assert
        Assert.Equal(ConfigurationPersistResult.Written, result);
        Assert.Same(_graph.Device, Assert.Single(_graph.Store.WrittenSubjects));
        Assert.Equal(1, _graph.Device.ApplyCount);
    }

    [Fact]
    public async Task WhenSubjectIsNestedInOwnerConfiguration_ThenOwnerIsWrittenAndApplied()
    {
        // Arrange
        var settings = new DeviceSettings(_graph.Context);
        _graph.Device.Settings = settings;

        // Act
        var result = await ConfigurationPersistence.PersistOwnerAsync(_graph.RootManager, settings, CancellationToken.None);

        // Assert
        Assert.Equal(ConfigurationPersistResult.Written, result);
        Assert.Same(_graph.Device, Assert.Single(_graph.Store.WrittenSubjects));
        Assert.Equal(1, _graph.Device.ApplyCount);
    }

    [Fact]
    public async Task WhenWriterWritesNothing_ThenNotWrittenIsReturnedAndOwnerIsStillApplied()
    {
        // Arrange
        _graph.Store.WritesConfiguration = false;

        // Act
        var result = await ConfigurationPersistence.PersistOwnerAsync(_graph.RootManager, _graph.Device, CancellationToken.None);

        // Assert
        Assert.Equal(ConfigurationPersistResult.NotWritten, result);
        Assert.Equal(1, _graph.Device.ApplyCount);
    }

    [Fact]
    public async Task WhenOwnerHasNoWriterParent_ThenRootManagerIsAskedAndWritesNothing()
    {
        // Arrange
        var device = new PersistedDevice(_graph.Context);

        // Act
        var result = await ConfigurationPersistence.PersistOwnerAsync(_graph.RootManager, device, CancellationToken.None);

        // Assert
        Assert.Equal(ConfigurationPersistResult.NotWritten, result);
        Assert.Empty(_graph.Store.WrittenSubjects);
        Assert.Equal(1, device.ApplyCount);
    }

    [Fact]
    public async Task WhenSubjectIsPartOfNoConfiguration_ThenNoOwnerIsReturnedAndNothingIsWritten()
    {
        // Arrange
        var settings = new DeviceSettings(_graph.Context);

        // Act
        var result = await ConfigurationPersistence.PersistOwnerAsync(_graph.RootManager, settings, CancellationToken.None);

        // Assert
        Assert.Equal(ConfigurationPersistResult.NoOwner, result);
        Assert.Empty(_graph.Store.WrittenSubjects);
    }

    public void Dispose()
    {
        _graph.Dispose();
    }
}
