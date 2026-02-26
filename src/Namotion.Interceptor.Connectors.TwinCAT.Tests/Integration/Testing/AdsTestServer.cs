using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinCAT.Ads;
using TwinCAT.Ads.Server;
using TwinCAT.Ads.Server.TypeSystem;
using TwinCAT.Ads.TcpRouter;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Testing;

/// <summary>
/// Manages an in-process ADS router and symbolic server for E2E integration testing.
/// </summary>
public sealed class AdsTestServer : IAsyncDisposable
{
    private const string AmsNetIdValue = "1.2.3.4.5.6";
    private const ushort AmsPort = 25000;
    private const int LoopbackPort = 44236;

    private readonly TestSymbol[] _symbols;
    private AmsTcpIpRouter? _router;
    private TestAdsSymbolicServer? _server;
    private CancellationTokenSource _serverCts = new();
    private CancellationTokenSource _routerCts = new();

    /// <summary>
    /// Gets the AMS Net ID string for client configuration.
    /// </summary>
    public string AmsNetIdString => AmsNetIdValue;

    /// <summary>
    /// Gets the AMS server port for client configuration.
    /// </summary>
    public int ServerPort => AmsPort;

    /// <summary>
    /// Gets the router configuration for the in-process AMS TCP/IP router.
    /// Used to configure custom loopback port so tests work without TwinCAT installed.
    /// </summary>
    public IConfiguration RouterConfiguration { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsTestServer"/> class.
    /// </summary>
    /// <param name="symbols">The symbols to register on the server.</param>
    public AdsTestServer(TestSymbol[] symbols)
    {
        _symbols = symbols;

        RouterConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AmsRouter:Name"] = "TestRouter",
                ["AmsRouter:NetId"] = AmsNetIdValue,
                ["AmsRouter:LoopbackPort"] = LoopbackPort.ToString(),
            })
            .Build();
    }

    /// <summary>
    /// Starts the in-process ADS router and symbolic server.
    /// </summary>
    public async Task StartAsync()
    {
        _routerCts = new CancellationTokenSource();
        _serverCts = new CancellationTokenSource();

        // Create and start the in-process TCP/IP router with custom loopback port
        _router = new AmsTcpIpRouter(
            AmsNetId.Parse(AmsNetIdValue),
            externalPort: 0,
            loopbackIP: IPAddress.Loopback,
            loopbackPort: LoopbackPort,
            loopbackCommunicationIPs: null,
            udpDiscoveryPort: 0,
            loggerFactory: null);
        _ = _router.StartAsync(_routerCts.Token);

        // Wait for the router to start up
        await Task.Delay(500);

        // Create and start the symbolic server
        _server = new TestAdsSymbolicServer(AmsPort, "TestServer", _symbols, RouterConfiguration, null);
        _ = _server.ConnectServerAndWaitAsync(_serverCts.Token);

        // Wait until the server is connected
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!_server.IsConnected && DateTime.UtcNow < timeout)
        {
            await Task.Delay(100);
        }

        if (!_server.IsConnected)
        {
            throw new InvalidOperationException("Test ADS server failed to connect within the timeout period.");
        }
    }

    /// <summary>
    /// Sets a symbol value on the server, triggering notifications to connected clients.
    /// </summary>
    /// <param name="path">The symbol path.</param>
    /// <param name="value">The new value.</param>
    public void SetSymbolValue(string path, object value)
    {
        if (_server is null)
        {
            throw new InvalidOperationException("Server is not running.");
        }

        _server.UpdateValue(path, value);
    }

    /// <summary>
    /// Gets a symbol value from the server.
    /// </summary>
    /// <param name="path">The symbol path.</param>
    /// <returns>The current value of the symbol.</returns>
    public object? GetSymbolValue(string path)
    {
        if (_server is null)
        {
            throw new InvalidOperationException("Server is not running.");
        }

        return _server.ReadValue(path);
    }

    /// <summary>
    /// Resets all symbol values to their initial values.
    /// Useful for test isolation when sharing a server across tests.
    /// </summary>
    public void ResetSymbolValues()
    {
        if (_server is null)
        {
            throw new InvalidOperationException("Server is not running.");
        }

        foreach (var symbol in _symbols)
        {
            if (symbol.InitialValue is not null)
            {
                _server.UpdateValue(symbol.Path, symbol.InitialValue);
            }
        }
    }

    /// <summary>
    /// Stops the ADS server (but not the router), useful for testing reconnection.
    /// </summary>
    public async Task StopAsync()
    {
        if (_server is not null)
        {
            _serverCts.Cancel();
            await Task.Delay(200);
            _server.Dispose();
            _server = null;
        }
    }

    /// <summary>
    /// Restarts the ADS server with the same symbols, useful for testing reconnection.
    /// </summary>
    public async Task RestartAsync()
    {
        await StopAsync();

        _serverCts = new CancellationTokenSource();
        _server = new TestAdsSymbolicServer(AmsPort, "TestServer", _symbols, RouterConfiguration, null);
        _ = _server.ConnectServerAndWaitAsync(_serverCts.Token);

        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!_server.IsConnected && DateTime.UtcNow < timeout)
        {
            await Task.Delay(100);
        }

        if (!_server.IsConnected)
        {
            throw new InvalidOperationException("Test ADS server failed to reconnect within the timeout period.");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            _serverCts.Cancel();
            await Task.Delay(200);
            _server.Dispose();
            _server = null;
        }

        if (_router is not null)
        {
            _routerCts.Cancel();
            _router.Stop();
            _router = null;
        }

        _serverCts.Dispose();
        _routerCts.Dispose();
    }
}

/// <summary>
/// An in-process ADS symbolic server for E2E testing.
/// Follows the Beckhoff AdsSymbolicServer sample pattern.
/// </summary>
internal sealed class TestAdsSymbolicServer : AdsSymbolicServer
{
    private readonly TestSymbol[] _testSymbols;
    private readonly Dictionary<ISymbol, object?> _symbolValues = new();
    private readonly SymbolicAnyTypeMarshaler _symbolMarshaler = new(Encoding.Unicode);

    /// <summary>
    /// Initializes a new instance of the <see cref="TestAdsSymbolicServer"/> class.
    /// </summary>
    /// <param name="port">The AMS port to register on.</param>
    /// <param name="name">The server name.</param>
    /// <param name="symbols">The symbols to register.</param>
    /// <param name="configuration">Optional router configuration for custom loopback port.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public TestAdsSymbolicServer(
        ushort port,
        string name,
        TestSymbol[] symbols,
        IConfiguration? configuration,
        ILoggerFactory? loggerFactory)
        : base(port, name, configuration, loggerFactory)
    {
        _testSymbols = symbols;
    }

    /// <inheritdoc/>
    protected override void OnConnected()
    {
        AddSymbols();
        base.OnConnected();
    }

    /// <summary>
    /// Updates a symbol value by path, triggering notifications to connected clients.
    /// </summary>
    /// <param name="path">The symbol path.</param>
    /// <param name="value">The new value.</param>
    public void UpdateValue(string path, object value)
    {
        // SetValue is a base class method that calls OnSetValue and triggers notifications
        SetValue(path, value);
    }

    /// <summary>
    /// Reads a symbol value by path.
    /// </summary>
    /// <param name="path">The symbol path.</param>
    /// <returns>The current value.</returns>
    public object? ReadValue(string path)
    {
        var symbol = Symbols[path];
        if (symbol is not null && _symbolValues.TryGetValue(symbol, out var value))
        {
            return value;
        }

        return null;
    }

    private void AddSymbols()
    {
        // Create a data area for all test symbols
        var globals = new DataArea("Globals", 0x02, 0x1000, 0x10000);

        // Track which types have been added to avoid duplicates
        var addedTypes = new HashSet<Type>();

        // Register types and add the data area
        foreach (var testSymbol in _testSymbols)
        {
            if (!addedTypes.Contains(testSymbol.DataType))
            {
                var beckhoffType = MapToBeckhoffType(testSymbol.DataType);
                symbolFactory!.AddType(beckhoffType);
                addedTypes.Add(testSymbol.DataType);
            }
        }

        symbolFactory!.AddDataArea(globals);

        // Register symbols
        foreach (var testSymbol in _testSymbols)
        {
            var beckhoffType = MapToBeckhoffType(testSymbol.DataType);
            symbolFactory!.AddSymbol(testSymbol.Path, beckhoffType, globals);
        }

        // Set initial values
        foreach (var testSymbol in _testSymbols)
        {
            if (testSymbol.InitialValue is not null)
            {
                _symbolValues[Symbols[testSymbol.Path]] = testSymbol.InitialValue;
            }
            else
            {
                _symbolValues[Symbols[testSymbol.Path]] = GetDefaultValue(testSymbol.DataType);
            }
        }
    }

    /// <inheritdoc/>
    protected override Task<ResultReadDeviceState> OnReadDeviceStateAsync(
        AmsAddress sender,
        uint invokeId,
        CancellationToken cancel)
    {
        var state = new StateInfo(AdsState.Run, 0);
        var result = ResultReadDeviceState.CreateSuccess(state);
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    protected override AdsErrorCode OnReadRawValue(ISymbol symbol, Span<byte> span)
    {
        var errorCode = OnGetValue(symbol, out var value);

        if (errorCode != AdsErrorCode.NoError)
        {
            return errorCode;
        }

        if (value is not null && _symbolMarshaler.TryMarshal(symbol, value, span, out _))
        {
            return AdsErrorCode.NoError;
        }

        return AdsErrorCode.DeviceInvalidSize;
    }

    /// <inheritdoc/>
    protected override AdsErrorCode OnWriteRawValue(ISymbol symbol, ReadOnlySpan<byte> span)
    {
        _symbolMarshaler.Unmarshal(symbol, span, null, out var value);
        return SetValue(symbol, value);
    }

    /// <inheritdoc/>
    protected override AdsErrorCode OnSetValue(ISymbol symbol, object value, out bool valueChanged)
    {
        valueChanged = false;

        if (!_symbolValues.ContainsKey(symbol))
        {
            return AdsErrorCode.DeviceSymbolNotFound;
        }

        var oldValue = _symbolValues[symbol];

        if (oldValue is null || !oldValue.Equals(value))
        {
            _symbolValues[symbol] = value;
            valueChanged = true;
        }

        return AdsErrorCode.NoError;
    }

    /// <inheritdoc/>
    protected override AdsErrorCode OnGetValue(ISymbol symbol, out object value)
    {
        if (_symbolValues.TryGetValue(symbol, out var storedValue))
        {
            value = storedValue!;
            return AdsErrorCode.NoError;
        }

        value = null!;
        return AdsErrorCode.DeviceSymbolNotFound;
    }

    private static DataType MapToBeckhoffType(Type type)
    {
        if (type == typeof(bool))
        {
            return new PrimitiveType("BOOL", typeof(bool));
        }

        if (type == typeof(short))
        {
            return new PrimitiveType("INT", typeof(short));
        }

        if (type == typeof(int))
        {
            return new PrimitiveType("DINT", typeof(int));
        }

        if (type == typeof(float))
        {
            return new PrimitiveType("REAL", typeof(float));
        }

        if (type == typeof(double))
        {
            return new PrimitiveType("LREAL", typeof(double));
        }

        if (type == typeof(string))
        {
            return new StringType(80, Encoding.Unicode);
        }

        throw new ArgumentException($"Unsupported type: {type.FullName}", nameof(type));
    }

    private static object? GetDefaultValue(Type type)
    {
        if (type == typeof(string))
        {
            return string.Empty;
        }

        if (type.IsValueType)
        {
            return Activator.CreateInstance(type);
        }

        return null;
    }
}
