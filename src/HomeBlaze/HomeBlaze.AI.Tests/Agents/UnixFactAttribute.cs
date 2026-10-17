using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

/// <summary>
/// A fact that needs a POSIX shell, because it runs a fake CLI written as a shell script.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Requires a POSIX shell.";
        }
    }
}
