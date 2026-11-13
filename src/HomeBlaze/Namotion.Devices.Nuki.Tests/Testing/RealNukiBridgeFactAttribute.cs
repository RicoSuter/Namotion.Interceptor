using Xunit;

namespace Namotion.Devices.Nuki.Tests.Testing;

/// <summary>
/// A test that runs only against a real bridge, whose address is in <see cref="HostVariable"/> and whose API token
/// is in <see cref="TokenVariable"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealNukiBridgeFactAttribute : FactAttribute
{
    /// <summary>The environment variable that holds the host address of the bridge.</summary>
    public const string HostVariable = "NUKI_HOST";

    /// <summary>The environment variable that holds the API token of the bridge.</summary>
    public const string TokenVariable = "NUKI_TOKEN";

    public RealNukiBridgeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TokenVariable)))
        {
            Skip = $"Reads a real Nuki bridge; set {HostVariable} and {TokenVariable} to run it.";
        }
    }
}
