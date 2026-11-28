namespace Namotion.ZWave.Tests.Hardware;

/// <summary>
/// A fact that runs only when <see cref="PortVariable"/> names the serial port of a real Z-Wave controller.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ZWaveHardwareFactAttribute : FactAttribute
{
    public const string PortVariable = "ZWAVE_PORT";

    public ZWaveHardwareFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PortVariable)))
        {
            Skip = $"Connects to a real Z-Wave controller; set {PortVariable} to its serial port (for example /dev/ttyACM0) to run it.";
        }
    }
}
