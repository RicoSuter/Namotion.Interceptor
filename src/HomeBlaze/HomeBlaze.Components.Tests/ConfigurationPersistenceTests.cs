using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Storage.Abstractions;
using MudBlazor;
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

    [Fact]
    public void WhenTheWriterOfTheOwnerRefuses_ThenTheCheckOfANestedSubjectThrowsTheRefusal()
    {
        // Arrange
        var settings = new DeviceSettings(_graph.Context);
        _graph.Device.Settings = settings;
        _graph.Store.Refusal = CreateRefusal();

        // Act
        var exception = Record.Exception(() => ConfigurationPersistence.ThrowIfOwnerWriteRefused(_graph.RootManager, settings));

        // Assert
        Assert.Same(_graph.Store.Refusal, exception);
    }

    [Fact]
    public void WhenSubjectIsPartOfNoConfiguration_ThenTheCheckDoesNotThrow()
    {
        // Arrange
        _graph.Store.Refusal = CreateRefusal();

        // Act
        var exception = Record.Exception(() => ConfigurationPersistence.ThrowIfOwnerWriteRefused(_graph.RootManager, new DeviceSettings(_graph.Context)));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenTheWriteIsRefused_ThenTheOwnerIsAppliedAndTheRefusalIsThrown()
    {
        // Arrange
        var refusal = CreateRefusal();
        _graph.Store.WriteException = refusal;

        // Act
        var exception = await Record.ExceptionAsync(() => ConfigurationPersistence.PersistOwnerAsync(_graph.RootManager, _graph.Device, CancellationToken.None));

        // Assert
        Assert.Same(refusal, exception);
        Assert.Equal(1, _graph.Device.ApplyCount);
    }

    [Fact]
    public void WhenTheWriterRefuses_ThenTheCheckOfAComponentShowsTheRefusalAsAnError()
    {
        // Arrange
        var snackbar = new SnackbarRecorder();
        _graph.Store.Refusal = CreateRefusal();

        // Act
        var canWrite = _graph.RootManager.CheckConfigurationWriteOrShowError(_graph.Device, snackbar.Snackbar);

        // Assert
        Assert.False(canWrite);
        var message = Assert.Single(snackbar.Messages);
        Assert.Equal(Severity.Error, message.Severity);
        Assert.Equal("Error saving configuration: refused", message.Message);
    }

    [Fact]
    public async Task WhenTheWriteOfAComponentFails_ThenTheErrorIsShownAndNothingIsThrown()
    {
        // Arrange
        var snackbar = new SnackbarRecorder();
        _graph.Store.WriteException = CreateRefusal();

        // Act
        var isWritten = await _graph.RootManager.WriteConfigurationOrWarnAsync(_graph.Device, snackbar.Snackbar, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        var message = Assert.Single(snackbar.Messages);
        Assert.Equal(Severity.Error, message.Severity);
        Assert.Equal("Error saving configuration: refused", message.Message);
    }

    private static ConfigurationWriteRefusedException CreateRefusal() => new("refused", ["the value of 'speed' could not be read"]);

    public void Dispose()
    {
        _graph.Dispose();
    }
}
