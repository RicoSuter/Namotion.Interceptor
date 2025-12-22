using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Samples;

/// <summary>
/// Sample motor subject with configuration and simulated sensor values.
/// </summary>
[InterceptorSubject]
public partial class Motor : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider
{
    // Configuration (persisted to JSON)

    /// <summary>
    /// Name of the motor.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    public string? Title => Name;

    public string? Icon => "Settings";

    [Derived]
    public string? IconColor => Status switch
    {
        MotorStatus.Running => "Success",
        MotorStatus.Error => "Error",
        _ => "Warning"
    };

    /// <summary>
    /// Target speed in RPM.
    /// </summary>
    [Configuration]
    [State("Target", Position = 2)]
    public partial int TargetSpeed { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Minimum")]
    public partial int TargetSpeed_Minimum { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Maximum")]
    public partial int TargetSpeed_Maximum { get; set; }

    /// <summary>
    /// Simulation update interval.
    /// </summary>
    [Configuration]
    public partial TimeSpan SimulationInterval { get; set; }

    // Live state (simulated, not persisted)

    /// <summary>
    /// Current speed in RPM.
    /// </summary>
    [State("Speed", Position = 3)]
    public partial int CurrentSpeed { get; set; }

    /// <summary>
    /// Current temperature in Celsius.
    /// </summary>
    [State(Position = 4, Unit = StateUnit.DegreeCelsius)]
    public partial double Temperature { get; set; }

    /// <summary>
    /// Current operational status.
    /// </summary>
    [State(Position = 1)]
    public partial MotorStatus Status { get; set; }

    // Derived properties

    /// <summary>
    /// Difference between target and current speed.
    /// </summary>
    [Derived]
    [State("Delta", Position = 5)]
    public int SpeedDelta => TargetSpeed - CurrentSpeed;

    /// <summary>
    /// Whether the motor is at target speed (within 50 RPM).
    /// </summary>
    [Derived]
    [State("At Target", Position = 6)]
    public bool IsAtTargetSpeed => Math.Abs(SpeedDelta) < 50;

    public Motor()
    {
        Name = string.Empty;
        TargetSpeed = 0;
        TargetSpeed_Minimum = 0;
        TargetSpeed_Maximum = 3000;
        SimulationInterval = TimeSpan.FromSeconds(1);
        Status = MotorStatus.Stopped;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status = MotorStatus.Starting;
        await Task.Delay(500, stoppingToken); // Simulate startup delay

        Status = MotorStatus.Running;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Simulate speed approaching target
                if (CurrentSpeed < TargetSpeed)
                {
                    CurrentSpeed = Math.Min(CurrentSpeed + 50, TargetSpeed);
                }
                else if (CurrentSpeed > TargetSpeed)
                {
                    CurrentSpeed = Math.Max(CurrentSpeed - 50, TargetSpeed);
                }

                // Simulate temperature based on speed
                var baseTemp = 25.0;
                var speedFactor = CurrentSpeed / 1000.0 * 20.0; // Higher speed = more heat
                var noise = (Random.Shared.NextDouble() - 0.5) * 2.0; // ±1°C noise
                Temperature = baseTemp + speedFactor + noise;

                await Task.Delay(SimulationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        // Shutdown sequence
        Status = MotorStatus.Stopping;
        while (CurrentSpeed > 0)
        {
            CurrentSpeed = Math.Max(CurrentSpeed - 100, 0);
            await Task.Delay(100, CancellationToken.None);
        }

        Status = MotorStatus.Stopped;
        Temperature = 25.0;
    }

    /// <summary>
    /// IConfigurableSubject implementation - called after configuration properties have been updated.
    /// </summary>
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // Properties are already updated by the storage container.
        // Override this method if you need to react to configuration changes
        // (e.g., restart a connection, recalculate derived values, etc.)
        return Task.CompletedTask;
    }

    // Operations

    /// <summary>
    /// Sets the target speed to the specified value.
    /// </summary>
    [Operation(Title = "Set Speed", Description = "Sets the motor target speed", Icon = "Speed", Position = 1)]
    public void SetTargetSpeed(int speed)
    {
        TargetSpeed = Math.Clamp(speed, TargetSpeed_Minimum, TargetSpeed_Maximum);
    }

    /// <summary>
    /// Immediately stops the motor by setting target speed to zero.
    /// </summary>
    [Operation(Title = "Emergency Stop", Description = "Immediately stops the motor", Icon = "Stop", Position = 2, RequiresConfirmation = true)]
    public void EmergencyStop()
    {
        TargetSpeed = 0;
        CurrentSpeed = 0;
        Status = MotorStatus.Stopped;
    }

    /// <summary>
    /// Gets the current motor diagnostics.
    /// </summary>
    [Query(Title = "Get Diagnostics", Description = "Returns current motor diagnostics", Icon = "Info", Position = 3)]
    public MotorDiagnostics GetDiagnostics()
    {
        return new MotorDiagnostics
        {
            Status = Status,
            CurrentSpeed = CurrentSpeed,
            TargetSpeed = TargetSpeed,
            Temperature = Temperature,
            IsAtTarget = IsAtTargetSpeed,
            SpeedDelta = SpeedDelta
        };
    }

    /// <summary>
    /// Simulates running for a specified duration.
    /// </summary>
    [Operation(Title = "Run Test", Description = "Runs the motor at specified speed for a duration", Icon = "PlayArrow", Position = 4)]
    public async Task RunTestAsync(int speed, int durationSeconds)
    {
        var previousSpeed = TargetSpeed;
        TargetSpeed = Math.Clamp(speed, TargetSpeed_Minimum, TargetSpeed_Maximum);

        await Task.Delay(TimeSpan.FromSeconds(durationSeconds));

        TargetSpeed = previousSpeed;
    }
}

/// <summary>
/// Motor diagnostics data.
/// </summary>
public class MotorDiagnostics
{
    public MotorStatus Status { get; set; }
    public int CurrentSpeed { get; set; }
    public int TargetSpeed { get; set; }
    public double Temperature { get; set; }
    public bool IsAtTarget { get; set; }
    public int SpeedDelta { get; set; }
}

public enum MotorStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}
