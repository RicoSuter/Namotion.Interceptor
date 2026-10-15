using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Samples;

/// <summary>
/// Sample motor subject with configuration and simulated sensor values.
/// </summary>
[Category("Samples")]
[Description("Simulated motor with speed control and temperature monitoring")]
[DisplayName("Motor")]
[SubjectIcon("Settings")]
[InterceptorSubject]
public partial class Motor : BackgroundService, IConfigurable, ITemperatureSensor, ITitleProvider, IIconProvider
{
    // Serializes the read-modify-write of a simulation step with EmergencyStop, so a step that read the speed
    // before the stop cannot overwrite it afterwards.
    private readonly Lock _simulationLock = new();

    // Configuration (persisted to JSON)

    /// <summary>
    /// Name of the motor.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    public string? Title => Name;

    public string? IconName => "Settings";

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
    [Category("Speed")]
    public partial int TargetSpeed { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), KnownAttributes.Minimum)]
    public partial int TargetSpeed_Minimum { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), KnownAttributes.Maximum)]
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
    [Category("Speed")]
    public partial int CurrentSpeed { get; set; }

    /// <summary>
    /// Current temperature in Celsius.
    /// </summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 4)]
    public partial decimal? Temperature { get; set; }

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
    [Category("Speed")]
    public int SpeedDelta => TargetSpeed - CurrentSpeed;

    /// <summary>
    /// Whether the motor is at target speed (within 50 RPM).
    /// </summary>
    [Derived]
    [State("At Target", Position = 6)]
    [Category("Speed")]
    public bool IsAtTargetSpeed => Math.Abs(SpeedDelta) < 50;

    // Runtime controls (public setters, not persisted)

    /// <summary>
    /// Whether the motor is powered. While not powered the simulation slows it down to 0 and keeps the target speed.
    /// </summary>
    [State("Powered", Position = 7)]
    public partial bool IsPowered { get; set; }

    /// <summary>
    /// Operating mode, which scales the speed the simulation aims at.
    /// </summary>
    [State(Position = 8)]
    public partial MotorMode Mode { get; set; }

    /// <summary>
    /// Mechanical load from 0 to 1, which raises the simulated temperature.
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 9)]
    public partial double Load { get; set; }

    [Derived]
    [PropertyAttribute(nameof(PowerOn), KnownAttributes.IsEnabled)]
    public bool PowerOn_IsEnabled => !IsPowered;

    [Derived]
    [PropertyAttribute(nameof(PowerOff), KnownAttributes.IsEnabled)]
    public bool PowerOff_IsEnabled => IsPowered;

    public Motor()
    {
        Name = string.Empty;
        TargetSpeed = 0;
        TargetSpeed_Minimum = 0;
        TargetSpeed_Maximum = 3000;
        SimulationInterval = TimeSpan.FromSeconds(1);
        Status = MotorStatus.Stopped;
        IsPowered = true;
        Mode = MotorMode.Normal;
        Load = 0.5;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status = MotorStatus.Starting;
        await Task.Delay(500, stoppingToken); // Simulate startup delay

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SimulateStep();
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
        Temperature = 25m;
    }

    /// <summary>
    /// Moves the current speed one step towards <see cref="GetSimulationTarget"/> and updates status and temperature.
    /// </summary>
    internal void SimulateStep()
    {
        lock (_simulationLock)
        {
            // Read once, so speed and status of this step agree when the power is switched concurrently.
            var isPowered = IsPowered;
            var target = isPowered ? GetPoweredSimulationTarget() : 0;

            var currentSpeed = CurrentSpeed;
            if (currentSpeed < target)
            {
                currentSpeed = Math.Min(currentSpeed + 50, target);
            }
            else if (currentSpeed > target)
            {
                currentSpeed = Math.Max(currentSpeed - 50, target);
            }

            CurrentSpeed = currentSpeed;

            if (isPowered)
            {
                Status = Status is MotorStatus.Stopped or MotorStatus.Stopping ? MotorStatus.Starting : MotorStatus.Running;
            }
            else
            {
                Status = currentSpeed > 0 ? MotorStatus.Stopping : MotorStatus.Stopped;
            }

            var baseTemperature = 25.0;
            var speedHeat = currentSpeed / 1000.0 * 20.0;
            var loadHeat = Load * 10.0;
            var noise = (Random.Shared.NextDouble() - 0.5) * 2.0; // ±1°C noise
            Temperature = (decimal)(baseTemperature + speedHeat + loadHeat + noise);
        }
    }

    /// <summary>
    /// Gets the speed the simulation aims at: 0 while not powered, else the target speed scaled by the mode and
    /// clamped to the range of the target speed.
    /// </summary>
    internal int GetSimulationTarget() => IsPowered ? GetPoweredSimulationTarget() : 0;

    private int GetPoweredSimulationTarget()
    {
        var modePercent = Mode switch
        {
            MotorMode.Eco => 60,
            MotorMode.Boost => 120,
            _ => 100
        };

        return (int)Math.Clamp((long)TargetSpeed * modePercent / 100, TargetSpeed_Minimum, TargetSpeed_Maximum);
    }

    /// <summary>
    /// IConfigurable implementation - called after configuration properties have been updated.
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
    [Category("Speed")]
    public void SetTargetSpeed(int speed)
    {
        TargetSpeed = Math.Clamp(speed, TargetSpeed_Minimum, TargetSpeed_Maximum);
    }

    /// <summary>
    /// Immediately stops the motor by powering it off. The target speed is kept, so <see cref="PowerOn"/> recovers it.
    /// </summary>
    [Operation(Title = "Emergency Stop", Description = "Immediately stops the motor", Icon = "Stop", Position = 2, RequiresConfirmation = true)]
    public void EmergencyStop()
    {
        lock (_simulationLock)
        {
            IsPowered = false;
            CurrentSpeed = 0;
            Status = MotorStatus.Stopped;
        }
    }

    /// <summary>
    /// Powers the motor on, so it speeds up to its target again.
    /// </summary>
    [Operation(Title = "Power On", Icon = "PowerSettingsNew", Position = 4)]
    public void PowerOn() => IsPowered = true;

    /// <summary>
    /// Powers the motor off, so it slows down to 0 and keeps its target speed.
    /// </summary>
    [Operation(Title = "Power Off", Icon = "PowerOff", Position = 5)]
    public void PowerOff() => IsPowered = false;

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
            Temperature = Temperature ?? 0,
            IsAtTarget = IsAtTargetSpeed,
            SpeedDelta = SpeedDelta
        };
    }

    /// <summary>
    /// Simulates running for a specified duration.
    /// </summary>
    [Operation(Title = "Run Test", Description = "Runs the motor at specified speed for a duration", Icon = "PlayArrow", Position = 6)]
    [Category("Speed")]
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
    public decimal Temperature { get; set; }
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

/// <summary>
/// Operating mode of a motor, which scales the speed it aims at.
/// </summary>
public enum MotorMode
{
    Eco,
    Normal,
    Boost
}
