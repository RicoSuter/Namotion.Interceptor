using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

/// <summary>
/// A fact that runs only when a <c>claude</c> executable is on PATH.
/// </summary>
public sealed class ClaudeCliFactAttribute : FactAttribute
{
    public ClaudeCliFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var executableName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var found = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, executableName)));
        if (!found)
        {
            Skip = "The claude CLI is not installed.";
        }
    }
}
