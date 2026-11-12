using HomeBlaze.Components.Controls;
using HomeBlaze.Components.Tests.Models;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

public sealed class PropertyWriterAccessTests : IDisposable
{
    private readonly PersistenceTestGraph _graph = new();
    private readonly SnackbarRecorder _snackbar = new();

    [Fact]
    public void WhenTheUserMayNotWrite_ThenTryWriteRefusesAndTheValueStays()
    {
        // Arrange
        _graph.Device.CurrentSpeed = 100;
        var currentSpeed = _graph.Device.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersistedDevice.CurrentSpeed))!;
        using var writer = new PropertyWriter(_graph.RootManager, _snackbar.Snackbar, TimeProvider.System, new TestUserPermissions { CannotWrite = { currentSpeed } });

        // Act
        var isWritten = writer.TryWrite(currentSpeed, 1500);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(100, _graph.Device.CurrentSpeed);
        var message = Assert.Single(_snackbar.Messages);
        Assert.Equal(Severity.Error, message.Severity);
        Assert.Contains("Your roles do not allow changing this.", message.Message);
    }

    [Fact]
    public void WhenTheUserMayWrite_ThenTryWriteSetsTheValue()
    {
        // Arrange
        _graph.Device.CurrentSpeed = 100;
        var currentSpeed = _graph.Device.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersistedDevice.CurrentSpeed))!;
        using var writer = new PropertyWriter(_graph.RootManager, _snackbar.Snackbar, TimeProvider.System, new TestUserPermissions());

        // Act
        var isWritten = writer.TryWrite(currentSpeed, 1500);

        // Assert
        Assert.True(isWritten);
        Assert.Equal(1500, _graph.Device.CurrentSpeed);
    }

    [Fact]
    public void WhenTheUserMayAdministerAConfigurationProperty_ThenTryWriteSetsItWhateverWriteAllows()
    {
        // Arrange
        _graph.Device.Speed = 100;
        var speed = _graph.Device.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersistedDevice.Speed))!;
        using var writer = new PropertyWriter(_graph.RootManager, _snackbar.Snackbar, TimeProvider.System, new TestUserPermissions { CannotWrite = { speed } });

        // Act
        var isWritten = writer.TryWrite(speed, 1500);

        // Assert
        Assert.True(isWritten);
        Assert.Equal(1500, _graph.Device.Speed);
    }

    [Fact]
    public void WhenTheUserMayNotAdministerAConfigurationProperty_ThenTryWriteRefusesAlthoughWriteIsAllowed()
    {
        // Arrange
        _graph.Device.Speed = 100;
        var speed = _graph.Device.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersistedDevice.Speed))!;
        using var writer = new PropertyWriter(_graph.RootManager, _snackbar.Snackbar, TimeProvider.System, new TestUserPermissions { CannotAdminister = { speed } });

        // Act
        var isWritten = writer.TryWrite(speed, 1500);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(100, _graph.Device.Speed);
        Assert.Contains("Your roles do not allow changing this.", Assert.Single(_snackbar.Messages).Message);
    }

    public void Dispose() => _graph.Dispose();
}
