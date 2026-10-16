using HomeBlaze.Components.Controls;
using HomeBlaze.Components.Tests.Models;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Components.Tests;

public sealed class PropertyWriterTests : IDisposable
{
    private readonly PersistenceTestGraph _graph = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly SnackbarRecorder _snackbar = new();
    private readonly PropertyWriter _writer;

    public PropertyWriterTests()
    {
        _writer = new PropertyWriter(_graph.RootManager, _snackbar.Snackbar, _timeProvider);
    }

    private static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string propertyName) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;

    [Fact]
    public void WhenPropertyIsMissing_ThenItIsNotWritable()
    {
        // Act
        var isWritable = PropertyWriter.IsWritable(null);

        // Assert
        Assert.False(isWritable);
    }

    [Theory]
    [InlineData(nameof(PersistedDevice.Speed), true)]
    [InlineData(nameof(PersistedDevice.CurrentSpeed), true)]
    [InlineData(nameof(PersistedDevice.Counter), false)]
    [InlineData(nameof(PersistedDevice.Reset), false)]
    public void WhenPropertyIsChecked_ThenOnlyAPublicSetterThatHoldsNoOperationIsWritable(string propertyName, bool expected)
    {
        // Arrange
        var property = GetProperty(_graph.Device, propertyName);

        // Act
        var isWritable = PropertyWriter.IsWritable(property);
        var isWritableWithReadValue = PropertyWriter.IsWritable(property, property.GetValue());

        // Assert
        Assert.Equal(expected, isWritable);
        Assert.Equal(expected, isWritableWithReadValue);
    }

    [Fact]
    public void WhenValuesAreWritten_ThenTheyAreConvertedToThePropertyTypes()
    {
        // Arrange
        _graph.Device.Ratio = 0.5;

        // Act
        var isSpeedWritten = _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Speed)), 1500.0);
        var isModeWritten = _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Mode)), "Boost");
        var isRatioWritten = _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Ratio)), null);

        // Assert
        Assert.True(isSpeedWritten);
        Assert.True(isModeWritten);
        Assert.True(isRatioWritten);
        Assert.Equal(1500, _graph.Device.Speed);
        Assert.Equal(DeviceMode.Boost, _graph.Device.Mode);
        Assert.Null(_graph.Device.Ratio);
        Assert.Empty(_snackbar.Messages);
    }

    [Theory]
    [InlineData(nameof(PersistedDevice.Mode), "Turbo")]
    [InlineData(nameof(PersistedDevice.Mode), "boost")]
    [InlineData(nameof(PersistedDevice.Mode), "1")]
    [InlineData(nameof(PersistedDevice.Speed), 1e12)]
    [InlineData(nameof(PersistedDevice.Speed), null)]
    public void WhenValueCannotBeConverted_ThenFalseIsReturnedAndAnErrorIsShown(string propertyName, object? value)
    {
        // Act
        var isWritten = _writer.TryWrite(GetProperty(_graph.Device, propertyName), value);

        // Assert
        Assert.False(isWritten);
        var message = Assert.Single(_snackbar.Messages);
        Assert.Equal(Severity.Error, message.Severity);
        Assert.StartsWith($"Could not set '{propertyName}': ", message.Message);
    }

    [Fact]
    public void WhenSetterRejectsValue_ThenFalseIsReturnedAndAnErrorIsShown()
    {
        // Act
        var isWritten = _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Speed)), 5000);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(0, _graph.Device.Speed);
        var message = Assert.Single(_snackbar.Messages);
        Assert.Equal(Severity.Error, message.Severity);
        Assert.StartsWith("Could not set 'Speed': ", message.Message);
    }

    [Fact]
    public void WhenPropertyIsNotWritable_ThenFalseIsReturnedAndAnErrorIsShown()
    {
        // Act
        var isWritten = _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Counter)), 5);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(0, _graph.Device.Counter);
        Assert.Equal(Severity.Error, Assert.Single(_snackbar.Messages).Severity);
    }

    [Fact]
    public async Task WhenConfigurationPropertyIsWrittenSeveralTimes_ThenOwnerIsPersistedOnceAfterTheLastWrite()
    {
        // Arrange
        var speed = GetProperty(_graph.Device, nameof(PersistedDevice.Speed));

        // Act
        _writer.TryWrite(speed, 100);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        _writer.TryWrite(speed, 200);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        _writer.TryWrite(speed, 300);
        _timeProvider.Advance(PropertyWriter.PersistDelay - TimeSpan.FromMilliseconds(1));
        var writeCountBeforeDelay = _graph.Store.WrittenSubjects.Count;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, writeCountBeforeDelay);
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Device.ApplyCount == 1);
        Assert.Same(_graph.Device, Assert.Single(_graph.Store.WrittenSubjects));
        Assert.Equal(300, _graph.Device.Speed);
        Assert.Empty(_snackbar.Messages);
    }

    [Fact]
    public async Task WhenTwoOwnersAreWritten_ThenEachIsPersistedAfterItsOwnLastWrite()
    {
        // Arrange
        var otherDevice = new PersistedDevice(_graph.Context);
        _graph.Store.OtherDevice = otherDevice;

        // Act
        _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Speed)), 100);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        _writer.TryWrite(GetProperty(otherDevice, nameof(PersistedDevice.Speed)), 200);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Store.WrittenSubjects.Count == 1);
        var firstWritten = _graph.Store.WrittenSubjects.ToArray();
        _timeProvider.Advance(TimeSpan.FromMilliseconds(500));

        // Assert
        Assert.Same(_graph.Device, Assert.Single(firstWritten));
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Store.WrittenSubjects.Count == 2);
        Assert.Same(otherDevice, _graph.Store.WrittenSubjects.Last());
    }

    [Fact]
    public void WhenStatePropertyIsWritten_ThenNothingIsPersisted()
    {
        // Act
        var isWritten = _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.CurrentSpeed)), 700);
        _timeProvider.Advance(PropertyWriter.PersistDelay * 2);

        // Assert
        Assert.True(isWritten);
        Assert.Equal(700, _graph.Device.CurrentSpeed);
        Assert.Empty(_graph.Store.WrittenSubjects);
        Assert.Equal(0, _graph.Device.ApplyCount);
        Assert.Empty(_snackbar.Messages);
    }

    [Fact]
    public void WhenValueIsUnchanged_ThenNothingIsPersisted()
    {
        // Arrange
        _graph.Device.Speed = 100;

        // Act
        var isWritten = _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Speed)), 100);
        _timeProvider.Advance(PropertyWriter.PersistDelay * 2);

        // Assert
        Assert.True(isWritten);
        Assert.Empty(_graph.Store.WrittenSubjects);
    }

    [Fact]
    public async Task WhenWriterWritesNothing_ThenNotSavedWarningIsShownAndValueStaysApplied()
    {
        // Arrange
        _graph.Store.WritesConfiguration = false;

        // Act
        _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Speed)), 100);
        _timeProvider.Advance(PropertyWriter.PersistDelay);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => _snackbar.Messages.Count == 1);
        var message = Assert.Single(_snackbar.Messages);
        Assert.Equal(Severity.Warning, message.Severity);
        Assert.Contains("not saved", message.Message);
        Assert.Equal(100, _graph.Device.Speed);
    }

    [Fact]
    public async Task WhenWriterThrows_ThenErrorIsShownAndValueStaysApplied()
    {
        // Arrange
        _graph.Store.WriteException = new IOException("The file is in use.");

        // Act
        _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Speed)), 100);
        _timeProvider.Advance(PropertyWriter.PersistDelay);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => _snackbar.Messages.Count == 1);
        var message = Assert.Single(_snackbar.Messages);
        Assert.Equal(Severity.Error, message.Severity);
        Assert.Contains("The file is in use.", message.Message);
        Assert.Equal(100, _graph.Device.Speed);
    }

    [Fact]
    public async Task WhenSubjectIsPartOfNoConfiguration_ThenNotSavedWarningIsShownAndValueStaysApplied()
    {
        // Arrange
        var settings = new DeviceSettings(_graph.Context);

        // Act
        var isWritten = _writer.TryWrite(GetProperty(settings, nameof(DeviceSettings.Level)), 3);
        _timeProvider.Advance(PropertyWriter.PersistDelay);

        // Assert
        Assert.True(isWritten);
        await AsyncTestHelpers.WaitUntilAsync(() => _snackbar.Messages.Count == 1);
        Assert.Equal(Severity.Warning, Assert.Single(_snackbar.Messages).Severity);
        Assert.Equal(3, settings.Level);
        Assert.Empty(_graph.Store.WrittenSubjects);
    }

    [Fact]
    public async Task WhenDisposedWithAPendingSave_ThenOwnerIsPersistedAtOnceAndNothingIsShown()
    {
        // Arrange
        _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Speed)), 100);

        // Act
        _writer.Dispose();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Device.ApplyCount == 1);
        _timeProvider.Advance(PropertyWriter.PersistDelay * 2);
        Assert.Single(_graph.Store.WrittenSubjects);
        Assert.Empty(_snackbar.Messages);
    }

    [Fact]
    public async Task WhenOwnerIsWrittenWhileItIsSaved_ThenTheNextSaveStartsAfterTheRunningOneWithTheLastValue()
    {
        // Arrange
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _graph.Store.WriteGate = writeGate.Task;
        var speed = GetProperty(_graph.Device, nameof(PersistedDevice.Speed));
        _writer.TryWrite(speed, 100);
        _timeProvider.Advance(PropertyWriter.PersistDelay);
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Store.WrittenSpeeds.Count == 1);

        // Act
        _writer.TryWrite(speed, 200);
        _timeProvider.Advance(PropertyWriter.PersistDelay);
        var startedWritesWhileFirstRuns = _graph.Store.WrittenSpeeds.Count;
        writeGate.SetResult();

        // Assert
        Assert.Equal(1, startedWritesWhileFirstRuns);
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Device.ApplyCount == 2);
        Assert.Equal([100, 200], _graph.Store.WrittenSpeeds);
        Assert.Equal(2, _graph.Store.WrittenSubjects.Count);
        Assert.Empty(_snackbar.Messages);
    }

    [Fact]
    public async Task WhenWriterIsDisposedWhileASaveRuns_ThenItsResultIsNotShown()
    {
        // Arrange
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _graph.Store.WriteGate = writeGate.Task;
        _graph.Store.WritesConfiguration = false;
        var speed = GetProperty(_graph.Device, nameof(PersistedDevice.Speed));
        _writer.TryWrite(speed, 100);
        _timeProvider.Advance(PropertyWriter.PersistDelay);
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Store.WrittenSpeeds.Count == 1);
        _writer.TryWrite(speed, 200);

        // Act
        _writer.Dispose();
        writeGate.SetResult();

        // Assert
        // The save that Dispose starts for the second write runs only after the running save has completed.
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Store.WrittenSubjects.Count == 2);
        Assert.Equal([100, 200], _graph.Store.WrittenSpeeds);
        Assert.Empty(_snackbar.Messages);
    }

    [Fact]
    public async Task WhenWriterIsDisposedDuringAWrite_ThenOwnerIsPersistedAtOnceAndNothingIsShown()
    {
        // Arrange
        _graph.Device.SpeedChanged = _writer.Dispose;

        // Act
        var isWritten = _writer.TryWrite(GetProperty(_graph.Device, nameof(PersistedDevice.Speed)), 100);

        // Assert
        Assert.True(isWritten);
        await AsyncTestHelpers.WaitUntilAsync(() => _graph.Device.ApplyCount == 1);
        Assert.Same(_graph.Device, Assert.Single(_graph.Store.WrittenSubjects));
        Assert.Empty(_snackbar.Messages);
    }

    public void Dispose()
    {
        // Runs the saves that a test left pending on this thread, so none of them outlives the graph.
        _timeProvider.Advance(PropertyWriter.PersistDelay);
        _writer.Dispose();
        _graph.Dispose();
    }
}
