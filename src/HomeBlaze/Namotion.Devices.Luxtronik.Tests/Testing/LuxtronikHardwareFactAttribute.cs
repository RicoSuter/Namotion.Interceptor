namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// A fact that runs only when LUXTRONIK_HOST names a real Luxtronik 2.1 controller with the Smart Home Interface enabled.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LuxtronikHardwareFactAttribute : FactAttribute
{
    public LuxtronikHardwareFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUXTRONIK_HOST")))
        {
            Skip = "Set LUXTRONIK_HOST to the controller's IP address to run hardware tests.";
        }
    }
}
