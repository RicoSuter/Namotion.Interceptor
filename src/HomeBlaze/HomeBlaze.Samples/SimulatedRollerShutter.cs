using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Covers;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Samples;

/// <summary>
/// A simulated roller shutter that moves from fully open to fully closed in <see cref="TravelDuration"/>. An operation
/// starts the move and returns, as a real cover does, and the shutter moves until it reaches its target or is stopped.
/// </summary>
[Category("Samples")]
[Description("Simulated roller shutter")]
[DisplayName("Simulated roller shutter")]
[SubjectIcon("Blinds")]
[InterceptorSubject]
public partial class SimulatedRollerShutter : IConfigurable, IRollerShutter, ITitleProvider, IIconProvider
{
    private const int StepsPerTravel = 20;

    // Guards the generation together with the state writes that depend on it.
    private readonly System.Threading.Lock _moveLock = new();
    private int _moveGeneration;

    public SimulatedRollerShutter()
    {
        Name = string.Empty;
        TravelDuration = TimeSpan.FromSeconds(10);
        Position = 0m;
        ShutterState = RollerShutterState.Open;
    }

    /// <summary>Name of the shutter.</summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>How long the shutter takes from fully open to fully closed.</summary>
    [Configuration]
    public partial TimeSpan TravelDuration { get; set; }

    public partial decimal? Position { get; set; }

    public partial RollerShutterState? ShutterState { get; set; }

    [Derived]
    public bool? IsMoving => ShutterState is RollerShutterState.Opening or RollerShutterState.Closing;

    public bool? IsCalibrating => false;

    public string? Title => Name;

    [Derived]
    public string? IconName => IsMoving == true ? "BlindsClosed" : "Blinds";

    [Derived]
    [PropertyAttribute("Open", KnownAttributes.IsEnabled)]
    public bool Open_IsEnabled => ShutterState is not (RollerShutterState.Open or RollerShutterState.Opening);

    [Derived]
    [PropertyAttribute("Close", KnownAttributes.IsEnabled)]
    public bool Close_IsEnabled => ShutterState is not (RollerShutterState.Closed or RollerShutterState.Closing);

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => IsMoving == true;

    [Derived]
    [PropertyAttribute("Open", KnownAttributes.IsActive)]
    public bool Open_IsActive => ShutterState == RollerShutterState.Opening;

    [Derived]
    [PropertyAttribute("Close", KnownAttributes.IsActive)]
    public bool Close_IsActive => ShutterState == RollerShutterState.Closing;

    [Operation(Title = "Open", Icon = "KeyboardArrowUp", Position = 1)]
    public Task OpenAsync(CancellationToken cancellationToken)
    {
        StartMove(0m);
        return Task.CompletedTask;
    }

    [Operation(Title = "Close", Icon = "KeyboardArrowDown", Position = 2)]
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        StartMove(1m);
        return Task.CompletedTask;
    }

    [Operation(Title = "Stop", Icon = "Stop", Position = 3)]
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_moveLock)
        {
            _moveGeneration++;
            ShutterState = GetStateAt(Position ?? 0m);
        }

        return Task.CompletedTask;
    }

    [Operation(Position = 4)]
    public Task SetPositionAsync(
        [OperationParameter(Unit = StateUnit.Percent)] decimal position,
        CancellationToken cancellationToken)
    {
        StartMove(Math.Clamp(position, 0m, 1m));
        return Task.CompletedTask;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private void StartMove(decimal target)
    {
        int generation;
        lock (_moveLock)
        {
            var position = Position ?? 0m;
            generation = ++_moveGeneration;
            ShutterState = target == position ? GetStateAt(position) :
                target < position ? RollerShutterState.Opening : RollerShutterState.Closing;
            if (target == position)
            {
                return;
            }
        }

        _ = MoveAsync(generation, target);
    }

    private async Task MoveAsync(int generation, decimal target)
    {
        const decimal step = 1m / StepsPerTravel;
        var interval = TravelDuration / StepsPerTravel;
        while (true)
        {
            if (interval > TimeSpan.Zero)
            {
                await Task.Delay(interval);
            }

            // A newer move or a stop replaces this one.
            lock (_moveLock)
            {
                if (_moveGeneration != generation)
                {
                    return;
                }

                var position = Position ?? 0m;
                position = position < target ? Math.Min(position + step, target) : Math.Max(position - step, target);
                Position = position;
                if (position == target)
                {
                    ShutterState = GetStateAt(position);
                    return;
                }
            }
        }
    }

    private static RollerShutterState GetStateAt(decimal position) => position switch
    {
        0m => RollerShutterState.Open,
        1m => RollerShutterState.Closed,
        _ => RollerShutterState.PartiallyOpen
    };
}
