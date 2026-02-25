using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Resilience;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Manages the ADS connection lifecycle including session creation, circuit breaker,
/// event subscriptions, reconnection tracking, and ADS state monitoring.
/// </summary>
internal sealed class AdsConnectionManager : IAsyncDisposable
{
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;

    // ADS connection objects
    private AdsSession? _session;
    private IAdsConnection? _connection;
    private ISymbolLoader? _symbolLoader;

    // First-occurrence logging state (Warning first, then Debug until cleared)
    private readonly ConcurrentDictionary<string, bool> _loggedErrors = new();

    // Reconnection tracking
    private long _totalReconnectionAttempts;
    private long _successfulReconnections;
    private long _failedReconnections;
    private long _lastConnectedAtTicks;

    // ADS state tracking
    private int _currentAdsState = -1; // -1 = not set, otherwise cast to AdsState
    private AdsState? _previousAdsState;

    private int _disposed; // 0 = false, 1 = true

    /// <summary>
    /// Fired when a previously-lost connection is restored.
    /// </summary>
    internal event Action? ConnectionRestored;

    /// <summary>
    /// Fired when the connection is lost.
    /// </summary>
    internal event Action? ConnectionLost;

    /// <summary>
    /// Fired when the PLC enters the Run state.
    /// </summary>
    internal event Action? AdsStateEnteredRun;

    /// <summary>
    /// Fired when the PLC symbol version changes.
    /// </summary>
    internal event Action? SymbolVersionChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsConnectionManager"/> class.
    /// </summary>
    public AdsConnectionManager(AdsClientConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _configuration = configuration;
        _logger = logger;

        _circuitBreaker = new CircuitBreaker(
            configuration.CircuitBreakerFailureThreshold,
            configuration.CircuitBreakerCooldown);
    }

    /// <summary>
    /// Gets the current ADS connection, or null if not connected.
    /// </summary>
    internal IAdsConnection? Connection => _connection;

    /// <summary>
    /// Gets the current symbol loader, or null if not loaded.
    /// </summary>
    internal ISymbolLoader? SymbolLoader => _symbolLoader;

    /// <summary>
    /// Gets the current PLC ADS state, or null if not yet known.
    /// </summary>
    internal AdsState? CurrentAdsState
    {
        get
        {
            var value = Volatile.Read(ref _currentAdsState);
            return value == -1 ? null : (AdsState)value;
        }
    }

    /// <summary>
    /// Gets whether the ADS client is currently connected.
    /// </summary>
    internal bool IsConnected =>
        _connection is AdsClient client && client.IsConnected;

    internal long TotalReconnectionAttempts =>
        Interlocked.Read(ref _totalReconnectionAttempts);

    internal long SuccessfulReconnections =>
        Interlocked.Read(ref _successfulReconnections);

    internal long FailedReconnections =>
        Interlocked.Read(ref _failedReconnections);

    internal DateTimeOffset? LastConnectedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastConnectedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    internal bool IsCircuitBreakerOpen => _circuitBreaker.IsOpen;

    internal long CircuitBreakerTripCount => _circuitBreaker.TripCount;

    /// <summary>
    /// Connects to the PLC with retry and circuit breaker logic.
    /// </summary>
    internal async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_circuitBreaker.ShouldAttempt())
                {
                    Interlocked.Increment(ref _totalReconnectionAttempts);

                    var amsNetId = AmsNetId.Parse(_configuration.AmsNetId);
                    var amsAddress = new AmsAddress(amsNetId, _configuration.AmsPort);

                    _session = _configuration.SessionSettings is not null
                        ? new AdsSession(amsAddress, _configuration.SessionSettings)
                        : new AdsSession(amsAddress);

                    var sessionConnection = _session.Connect();
                    _connection = _session.Connection as IAdsConnection;

                    // Subscribe to connection state changes
                    if (_connection is AdsConnection adsConnection)
                    {
                        adsConnection.ConnectionStateChanged += OnConnectionStateChanged;
                    }

                    // Subscribe to ADS state changes via the underlying client
                    if (_connection is AdsClient adsClient)
                    {
                        adsClient.AdsStateChanged += OnAdsStateChanged;
                        adsClient.AdsSymbolVersionChanged += OnSymbolVersionChanged;
                    }

                    Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                    _circuitBreaker.RecordSuccess();
                    _logger.LogInformation(
                        "Connected to TwinCAT PLC at {AmsNetId}:{Port}.",
                        _configuration.AmsNetId, _configuration.AmsPort);
                    return;
                }
                else
                {
                    _logger.LogDebug(
                        "Circuit breaker open, skipping connection attempt. Cooldown remaining: {Remaining}",
                        _circuitBreaker.GetCooldownRemaining());
                }
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failedReconnections);
                LogFirstOccurrence("Connection", exception,
                    "Failed to connect to TwinCAT PLC. Retrying...");
                _circuitBreaker.RecordFailure();
            }

            await Task.Delay(_configuration.HealthCheckInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a new symbol loader from the current session/connection.
    /// </summary>
    internal void RecreateSymbolLoader()
    {
        if (_session is not null)
        {
            _symbolLoader = SymbolLoaderFactory.Create(
                _connection!,
                new SymbolLoaderSettings(SymbolsLoadMode.DynamicTree));
        }
    }

    #region First-Occurrence Logging

    internal void LogFirstOccurrence(string category, Exception? exception, string message, params object[] arguments)
    {
        if (_loggedErrors.TryAdd(category, true))
        {
            if (exception is not null)
            {
                _logger.LogWarning(exception, message, arguments);
            }
            else
            {
                _logger.LogWarning(message, arguments);
            }
        }
        else
        {
            if (exception is not null)
            {
                _logger.LogDebug(exception, message, arguments);
            }
            else
            {
                _logger.LogDebug(message, arguments);
            }
        }
    }

    internal void ClearFirstOccurrenceLog(string category)
    {
        _loggedErrors.TryRemove(category, out _);
    }

    #endregion

    #region Event Handlers

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs eventArgs)
    {
        if (eventArgs.NewState == ConnectionState.Connected &&
            eventArgs.OldState != ConnectionState.Connected)
        {
            Interlocked.Increment(ref _successfulReconnections);
            ConnectionRestored?.Invoke();
        }
        else if (eventArgs.NewState != ConnectionState.Connected)
        {
            ClearFirstOccurrenceLog("Connection");
            ConnectionLost?.Invoke();
        }
    }

    private void OnAdsStateChanged(object? sender, AdsStateChangedEventArgs eventArgs)
    {
        var newState = eventArgs.State.AdsState;
        Volatile.Write(ref _currentAdsState, (int)newState);

        if (newState == AdsState.Run && _previousAdsState != AdsState.Run)
        {
            AdsStateEnteredRun?.Invoke();
        }
        else if (newState != AdsState.Run)
        {
            LogFirstOccurrence("PlcState", null,
                "PLC left Run state: {State}. Writes paused.", newState);
        }

        _previousAdsState = newState;
    }

    private void OnSymbolVersionChanged(object? sender, AdsSymbolVersionChangedEventArgs eventArgs)
    {
        SymbolVersionChanged?.Invoke();
    }

    #endregion

    #region Dispose

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        // Unsubscribe from events
        if (_connection is AdsConnection adsConnection)
        {
            adsConnection.ConnectionStateChanged -= OnConnectionStateChanged;
        }

        if (_connection is AdsClient adsClient)
        {
            adsClient.AdsStateChanged -= OnAdsStateChanged;
            adsClient.AdsSymbolVersionChanged -= OnSymbolVersionChanged;
        }

        // Dispose session (which disposes connection)
        if (_session is not null)
        {
            try
            {
                _session.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Error disposing ADS session.");
            }

            _session = null;
            _connection = null;
        }

        _symbolLoader = null;

        return ValueTask.CompletedTask;
    }

    #endregion
}
