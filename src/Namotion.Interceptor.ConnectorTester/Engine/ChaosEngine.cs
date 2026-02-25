using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Randomly disrupts a participant's connector by killing or disconnecting it.
/// The connector's background loop auto-restarts after KillAsync; the SDK's
/// reconnection logic handles recovery after DisconnectAsync.
/// </summary>
public class ChaosEngine : BackgroundService
{
    private readonly string _targetName;
    private readonly ChaosConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private IFaultInjectable? _target;
    private readonly ILogger _logger;
    private readonly Random _random = new();

    private volatile bool _isDisrupted;
    private readonly Lock _eventLock = new();
    private readonly List<ChaosEventRecord> _eventHistory = [];
    private long _chaosEventCount;
    private DateTimeOffset _currentEventStart;

    public string TargetName => _targetName;

    public long ChaosEventCount => Interlocked.Read(ref _chaosEventCount);

    public IReadOnlyList<ChaosEventRecord> EventHistory
    {
        get
        {
            lock (_eventLock)
            {
                return _eventHistory.ToList();
            }
        }
    }

    /// <summary>
    /// When false, the engine skips chaos actions but continues running.
    /// Controlled by VerificationEngine based on the active chaos profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public ChaosEngine(
        string targetName,
        ChaosConfiguration configuration,
        TestCycleCoordinator coordinator,
        IFaultInjectable? target,
        ILogger logger)
    {
        _targetName = targetName;
        _configuration = configuration;
        _coordinator = coordinator;
        _target = target;
        _logger = logger;

        configuration.Validate();
    }

    public void SetTarget(IFaultInjectable target)
    {
        _target = target;
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _chaosEventCount, 0);
        lock (_eventLock)
        {
            _eventHistory.Clear();
        }
    }

    /// <summary>
    /// Marks active disruption as recovered. Called by verification engine before convergence check.
    /// </summary>
    public Task RecoverActiveDisruptionAsync(CancellationToken cancellationToken)
    {
        if (_isDisrupted)
        {
            _isDisrupted = false;
            _logger.LogInformation("Chaos: {Target} recovered from disruption", _targetName);
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChaosEngine [{Target}] started (mode={Mode})", _targetName, _configuration.Mode);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(stoppingToken);

                // Wait random interval before next chaos event
                var interval = RandomTimeSpan(_configuration.IntervalMin, _configuration.IntervalMax);
                await Task.Delay(interval, stoppingToken);

                _coordinator.WaitIfPaused(stoppingToken);

                if (!Enabled)
                {
                    continue;
                }

                if (_target == null)
                {
                    _logger.LogWarning("Chaos: skipped on {Target} - no target available", _targetName);
                    continue;
                }

                var faultType = PickFaultType();
                _logger.LogWarning("Chaos: {FaultType} on {Target}", faultType, _targetName);
                _currentEventStart = DateTimeOffset.UtcNow;
                _isDisrupted = true;

                await _target.InjectFaultAsync(faultType, stoppingToken);

                // Hold disruption for random duration
                var duration = RandomTimeSpan(_configuration.DurationMin, _configuration.DurationMax);
                await Task.Delay(duration, stoppingToken);

                // Mark recovered
                _isDisrupted = false;
                var recoveredAt = DateTimeOffset.UtcNow;
                lock (_eventLock)
                {
                    _eventHistory.Add(new ChaosEventRecord(faultType, _currentEventStart, recoveredAt));
                }
                _logger.LogInformation("Chaos: {Target} recovered from {FaultType}", _targetName, faultType);
                Interlocked.Increment(ref _chaosEventCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private FaultType PickFaultType()
    {
        return _configuration.Mode.ToLowerInvariant() switch
        {
            "kill" => FaultType.Kill,
            "disconnect" => FaultType.Disconnect,
            _ => _random.Next(2) == 0 ? FaultType.Kill : FaultType.Disconnect
        };
    }

    private TimeSpan RandomTimeSpan(TimeSpan min, TimeSpan max)
    {
        var range = max - min;
        return min + TimeSpan.FromMilliseconds(_random.NextDouble() * range.TotalMilliseconds);
    }
}

public record ChaosEventRecord(FaultType FaultType, DateTimeOffset DisruptedAt, DateTimeOffset RecoveredAt)
{
    public TimeSpan Duration => RecoveredAt - DisruptedAt;
}
