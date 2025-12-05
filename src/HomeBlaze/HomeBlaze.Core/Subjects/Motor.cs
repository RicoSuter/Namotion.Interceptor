using HomeBlaze.Core.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Core.Subjects;

/// <summary>
/// Sample motor subject with configuration and simulated sensor values.
/// </summary>
[InterceptorSubject]
public partial class Motor : BackgroundService
{
    // Configuration (persisted to JSON)

    /// <summary>
    /// Name of the motor.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// Target speed in RPM.
    /// </summary>
    [Configuration]
    public partial int TargetSpeed { get; set; }

    /// <summary>
    /// Simulation update interval.
    /// </summary>
    [Configuration]
    public partial TimeSpan SimulationInterval { get; set; }

    // Live state (simulated, not persisted)

    /// <summary>
    /// Current speed in RPM.
    /// </summary>
    public partial int CurrentSpeed { get; set; }

    /// <summary>
    /// Current temperature in Celsius.
    /// </summary>
    public partial double Temperature { get; set; }

    /// <summary>
    /// Current operational status.
    /// </summary>
    public partial MotorStatus Status { get; set; }

    // Derived properties

    /// <summary>
    /// Difference between target and current speed.
    /// </summary>
    [Derived]
    public int SpeedDelta => TargetSpeed - CurrentSpeed;

    /// <summary>
    /// Whether the motor is at target speed (within 50 RPM).
    /// </summary>
    [Derived]
    public bool IsAtTargetSpeed => Math.Abs(SpeedDelta) < 50;

    public Motor()
    {
        Name = string.Empty;
        TargetSpeed = 0;
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
}

public enum MotorStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}
