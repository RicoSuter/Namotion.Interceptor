using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;
using Namotion.ZWave.Transport;

namespace Namotion.ZWave.Tests;

public class ZWaveControllerTests
{
    [Fact]
    public async Task WhenConnected_ThenTheStartupInformationIsRead()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();

        // Act
        await using var controller = await ZWaveController.ConnectAsync(stick).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal("Z-Wave 4.05", controller.LibraryVersion);
        Assert.Equal(ZWaveLibraryType.StaticController, controller.LibraryType);
        Assert.Equal(0xC0FFEE01u, controller.HomeId);
        Assert.Equal(1, controller.OwnNodeId);
        Assert.Equal("1.2", controller.SerialApiVersion);
        Assert.Equal(0x0086, controller.ManufacturerId);
        Assert.Equal(0x0001, controller.ProductType);
        Assert.Equal(0x005A, controller.ProductId);
        Assert.Equal(
            ZWaveControllerCapabilities.SisPresent | ZWaveControllerCapabilities.WasRealPrimary | ZWaveControllerCapabilities.Suc,
            controller.Capabilities);
        Assert.Equal(new byte[] { 1, 2, 5 }, controller.NodeIds);
        Assert.True(controller.SupportsFunction(FunctionId.GetControllerId));
        Assert.False(controller.SupportsFunction(0x13));
    }

    [Fact]
    public async Task WhenAStartupResponseIsInvalid_ThenConnectFailsAndTheStreamIsClosed()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        stick.SetResponse(FunctionId.GetControllerId, 0xC0);

        // Act & Assert
        await Assert.ThrowsAsync<ZWaveException>(() => ZWaveController.ConnectAsync(stick).WaitAsync(TestTimeouts.Default));
        Assert.True(stick.IsDisposed);
    }

    [Fact]
    public async Task WhenConnectIsCanceledDuringStartup_ThenItThrowsOperationCanceledAndTheStreamIsClosed()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        stick.AcknowledgesFrames = false;
        using var cancellation = new CancellationTokenSource();
        var options = new ZWaveControllerOptions { TimeProvider = new FakeTimeProvider() };
        var connect = ZWaveController.ConnectAsync(stick, options, cancellationToken: cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(TestTimeouts.Default));
        Assert.True(stick.IsDisposed);
    }

    [Fact]
    public async Task WhenOptionsAreInvalid_ThenConnectThrowsBeforeOpeningTheTransport()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        var options = new ZWaveControllerOptions { MaximumSendAttempts = 0 };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ZWaveController.ConnectAsync(stick, options).WaitAsync(TestTimeouts.Default));
        Assert.Empty(stick.ReceivedFrames);
    }

    [Theory]
    [InlineData(nameof(ZWaveControllerOptions.AcknowledgementTimeout))]
    [InlineData(nameof(ZWaveControllerOptions.ResponseTimeout))]
    [InlineData(nameof(ZWaveControllerOptions.FrameDeliveryTimeout))]
    public async Task WhenATimeoutExceedsTheTimerLimit_ThenConnectThrowsBeforeOpeningTheTransport(string option)
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        var tooLong = TimeSpan.FromMilliseconds(uint.MaxValue - 1) + TimeSpan.FromMilliseconds(1);
        var options = option switch
        {
            nameof(ZWaveControllerOptions.AcknowledgementTimeout) => new ZWaveControllerOptions { AcknowledgementTimeout = tooLong },
            nameof(ZWaveControllerOptions.ResponseTimeout) => new ZWaveControllerOptions { ResponseTimeout = tooLong },
            _ => new ZWaveControllerOptions { FrameDeliveryTimeout = tooLong }
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ZWaveController.ConnectAsync(stick, options).WaitAsync(TestTimeouts.Default));
        Assert.Empty(stick.ReceivedFrames);
    }

    [Fact]
    public async Task WhenTheFrameDeliveryTimeoutIsNotPositive_ThenConnectThrowsBeforeOpeningTheTransport()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        var options = new ZWaveControllerOptions { FrameDeliveryTimeout = TimeSpan.Zero };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ZWaveController.ConnectAsync(stick, options).WaitAsync(TestTimeouts.Default));
        Assert.Empty(stick.ReceivedFrames);
    }

    [Fact]
    public async Task WhenTheTransportFailsToOpen_ThenConnectThrowsAConnectionExceptionNamingTheTransport()
    {
        // Arrange
        var failure = new IOException("The port is busy.");
        var transport = new ThrowingTransport(failure);

        // Act
        var exception = await Record.ExceptionAsync(() => ZWaveController.ConnectAsync(transport).WaitAsync(TestTimeouts.Default));

        // Assert
        var connectionException = Assert.IsType<ZWaveConnectionException>(exception);
        Assert.Same(failure, connectionException.InnerException);
        Assert.Contains("the throwing transport", connectionException.Message);
    }

    [Fact]
    public async Task WhenTheLoggerFactoryIsDisposed_ThenConnectThrowsAConnectionExceptionAndLeavesNoStreamOpen()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();

        // Act
        var exception = await Record.ExceptionAsync(() => ZWaveController.ConnectAsync(stick, loggerFactory: new DisposedLoggerFactory()).WaitAsync(TestTimeouts.Default));

        // Assert
        var connectionException = Assert.IsType<ZWaveConnectionException>(exception);
        Assert.IsType<ObjectDisposedException>(connectionException.InnerException);
        Assert.False(stick.HasOpenStream);
    }

    [Fact]
    public async Task WhenTheSerialPortDoesNotExist_ThenConnectThrowsAConnectionExceptionNamingThePort()
    {
        // Arrange
        var transport = new SerialTransport("/dev/namotion-zwave-missing-port");

        // Act
        var exception = await Record.ExceptionAsync(() => ZWaveController.ConnectAsync(transport).WaitAsync(TestTimeouts.Default));

        // Assert
        var connectionException = Assert.IsType<ZWaveConnectionException>(exception);
        Assert.NotNull(connectionException.InnerException);
        Assert.Contains("/dev/namotion-zwave-missing-port", connectionException.Message);
    }

    [Fact]
    public async Task WhenOpeningTheTransportIsCanceled_ThenConnectThrowsOperationCanceled()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var transport = new ThrowingTransport(new OperationCanceledException(cancellation.Token));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ZWaveController.ConnectAsync(transport, cancellationToken: cancellation.Token).WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenTheTransportThrowsOperationCanceledWithoutTheCallerCanceling_ThenConnectThrowsAConnectionException()
    {
        // Arrange
        var transport = new ThrowingTransport(new OperationCanceledException());

        // Act
        var exception = await Record.ExceptionAsync(() => ZWaveController.ConnectAsync(transport).WaitAsync(TestTimeouts.Default));

        // Assert
        Assert.IsType<ZWaveConnectionException>(exception);
    }

    [Fact]
    public async Task WhenTheControllerSendsAnUnsolicitedFrame_ThenItIsAcknowledged()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        await using var controller = await ZWaveController.ConnectAsync(stick).WaitAsync(TestTimeouts.Default);
        var acksBefore = stick.ReceivedAckCount;

        // Act
        stick.SendFrame(FrameType.Request, 0x04, 0x00, 0x05, 0x03, 0x20, 0x03, 0xFF);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedAckCount == acksBefore + 1);
    }

    [Fact]
    public async Task WhenLoggingAnUnsolicitedFrameThrows_ThenTheNextFramesAreStillDispatched()
    {
        // Arrange
        var logger = new ThrowingLogger(LogLevel.Debug);
        var stick = FakeZWaveStick.CreateWithController();
        var controller = await ZWaveController.ConnectAsync(stick, loggerFactory: new ControllerLoggerFactory(logger)).WaitAsync(TestTimeouts.Default);

        // Act
        stick.SendFrame(FrameType.Request, 0x04, 0x00, 0x05, 0x03, 0x20, 0x03, 0xFF);
        stick.SendFrame(FrameType.Request, 0x04, 0x00, 0x02, 0x03, 0x20, 0x03, 0x00);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Count(entry => entry.Level == LogLevel.Debug) == 2);
        Assert.Null(await Record.ExceptionAsync(() => controller.DisposeAsync().AsTask().WaitAsync(TestTimeouts.Default)));
    }

    [Fact]
    public async Task WhenTheStickIsUnplugged_ThenCompletionFaults()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        await using var controller = await ZWaveController.ConnectAsync(stick).WaitAsync(TestTimeouts.Default);

        // Act
        stick.Disconnect();

        // Assert
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => controller.Completion.WaitAsync(TestTimeouts.Default));
    }

    // Hands the given logger to the controller and no logger to the driver.
    private sealed class ControllerLoggerFactory(ILogger controllerLogger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(ZWaveController).FullName ? controllerLogger : NullLogger.Instance;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class DisposedLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => throw new ObjectDisposedException(nameof(ILoggerFactory));

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingTransport(Exception exception) : IZWaveTransport
    {
        public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken) => ValueTask.FromException<Stream>(exception);

        public override string ToString() => "the throwing transport";
    }
}
