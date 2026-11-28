using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Transport;

namespace Namotion.ZWave;

/// <summary>
/// A connection to a Z-Wave controller. One instance is one connection: when <see cref="Completion"/> faults, the
/// instance starts closing its transport by itself. Dispose it before connecting again with a new instance:
/// <see cref="DisposeAsync"/> completes once the transport is closed, so the serial port is free again.
/// </summary>
public sealed class ZWaveController : IAsyncDisposable
{
    private readonly SerialApiDriver _driver;
    private readonly ILogger _logger;
    private readonly SerialApiCapabilities _serialApiCapabilities;
    private readonly Task _dispatchLoop;

    private ZWaveController(
        SerialApiDriver driver,
        ILogger logger,
        (string LibraryVersion, ZWaveLibraryType LibraryType) version,
        (uint HomeId, byte OwnNodeId) id,
        SerialApiCapabilities serialApiCapabilities,
        byte[] nodeIds,
        ZWaveControllerCapabilities capabilities)
    {
        _driver = driver;
        _logger = logger;
        _serialApiCapabilities = serialApiCapabilities;
        LibraryVersion = version.LibraryVersion;
        LibraryType = version.LibraryType;
        HomeId = id.HomeId;
        OwnNodeId = id.OwnNodeId;
        NodeIds = ImmutableCollectionsMarshal.AsImmutableArray(nodeIds);
        Capabilities = capabilities;
        _dispatchLoop = Task.Run(DispatchLoopAsync);
    }

    /// <summary>The version of the Z-Wave protocol library, such as "Z-Wave 4.05".</summary>
    public string LibraryVersion { get; }

    public ZWaveLibraryType LibraryType { get; }

    /// <summary>The ID of the controller's network.</summary>
    public uint HomeId { get; }

    /// <summary>The node ID of the controller in its network.</summary>
    public byte OwnNodeId { get; }

    /// <summary>The version of the controller's Serial API application, such as "1.2".</summary>
    public string SerialApiVersion => _serialApiCapabilities.Version;

    public ushort ManufacturerId => _serialApiCapabilities.ManufacturerId;

    public ushort ProductType => _serialApiCapabilities.ProductType;

    public ushort ProductId => _serialApiCapabilities.ProductId;

    public ZWaveControllerCapabilities Capabilities { get; }

    /// <summary>The IDs of the nodes in the network, including the controller, in ascending order.</summary>
    public ImmutableArray<byte> NodeIds { get; }

    /// <summary>
    /// Completes when the controller is disposed, and faults with <see cref="ZWaveConnectionException"/> when the connection fails.
    /// </summary>
    public Task Completion => _driver.Completion;

    /// <summary>
    /// Opens the transport and reads the controller's version, identity, capabilities and node list.
    /// </summary>
    /// <exception cref="ZWaveConnectionException">
    /// The transport could not be opened, with the transport's exception as the inner exception, or the connection
    /// failed during startup.
    /// </exception>
    /// <exception cref="ZWaveException">The controller did not answer as expected; the transport is closed again.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled; the transport is closed again.</exception>
    public static async Task<ZWaveController> ConnectAsync(
        IZWaveTransport transport,
        ZWaveControllerOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        options ??= new ZWaveControllerOptions();
        options.Validate();
        // Before the transport is opened, so that a factory that throws, such as one disposed at shutdown, leaves no stream open.
        var (driverLogger, logger) = CreateLoggers(transport, loggerFactory ?? NullLoggerFactory.Instance);

        var stream = await OpenAsync(transport, cancellationToken).ConfigureAwait(false);
        var driver = new SerialApiDriver(stream, options, driverLogger);
        try
        {
            driver.Start();

            var version = StartupResponses.ParseControllerVersion(await RequestAsync(FunctionId.GetControllerVersion).ConfigureAwait(false));
            var id = StartupResponses.ParseControllerId(await RequestAsync(FunctionId.GetControllerId).ConfigureAwait(false));
            var serialApiCapabilities = StartupResponses.ParseSerialApiCapabilities(await RequestAsync(FunctionId.GetSerialApiCapabilities).ConfigureAwait(false));
            var nodeIds = StartupResponses.ParseNodeIds(await RequestAsync(FunctionId.GetSerialApiInitData).ConfigureAwait(false));
            var capabilities = StartupResponses.ParseControllerCapabilities(await RequestAsync(FunctionId.GetControllerCapabilities).ConfigureAwait(false));

            logger.TryLog(
                LogLevel.Information,
                null,
                "Connected to Z-Wave controller {LibraryVersion} with home ID 0x{HomeId:X8}, node ID {OwnNodeId} and {NodeCount} nodes.",
                version.LibraryVersion, id.HomeId, id.OwnNodeId, nodeIds.Length);

            return new ZWaveController(driver, logger, version, id, serialApiCapabilities, nodeIds, capabilities);
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        async Task<byte[]> RequestAsync(byte function) =>
            (await driver.SendRequestAsync(function, default, cancellationToken).ConfigureAwait(false)).Payload;
    }

    private static (ILogger DriverLogger, ILogger ControllerLogger) CreateLoggers(IZWaveTransport transport, ILoggerFactory loggerFactory)
    {
        try
        {
            return (loggerFactory.CreateLogger<SerialApiDriver>(), loggerFactory.CreateLogger<ZWaveController>());
        }
        catch (Exception exception)
        {
            throw new ZWaveConnectionException($"Creating the loggers of the connection to the Z-Wave controller on {transport} failed.", exception);
        }
    }

    private static async Task<Stream> OpenAsync(IZWaveTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            return await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not ZWaveConnectionException
            && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            // Also an OperationCanceledException the caller did not cause, so that only the caller's cancellation ends a reconnect loop.
            throw new ZWaveConnectionException($"Opening the connection to the Z-Wave controller on {transport} failed.", exception);
        }
    }

    /// <summary>
    /// Whether the controller's Serial API supports the function with the given ID.
    /// </summary>
    internal bool SupportsFunction(byte functionId) => _serialApiCapabilities.SupportsFunction(functionId);

    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync().ConfigureAwait(false);
        await _dispatchLoop.ConfigureAwait(false);
    }

    private async Task DispatchLoopAsync()
    {
        await foreach (var frame in _driver.UnsolicitedFrames.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Dispatch(frame);
            }
            catch (Exception exception)
            {
                LogDispatchFailure(exception, frame);
            }
        }
    }

    private void Dispatch(DataFrame frame)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Received function 0x{Function:X2} with {Length} bytes.", frame.Function, frame.Payload.Length);
        }
    }

    private void LogDispatchFailure(Exception exception, DataFrame frame) =>
        _logger.TryLog(LogLevel.Warning, exception, "Handling function 0x{Function:X2} failed.", frame.Function);
}