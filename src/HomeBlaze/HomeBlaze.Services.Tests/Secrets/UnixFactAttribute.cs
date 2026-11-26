using Xunit;

namespace HomeBlaze.Services.Tests.Secrets;

/// <summary>
/// A fact that needs Unix file modes, which Windows does not have.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Requires Unix file modes.";
        }
    }
}
