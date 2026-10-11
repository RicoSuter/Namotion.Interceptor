using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

/// <summary>
/// A fact that runs only when <see cref="OptInVariable"/> is set to 1 and a <c>claude</c> executable is on PATH,
/// because it spends the local Claude subscription.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ClaudeCliFactAttribute : FactAttribute
{
    /// <summary>The environment variable that opts in to running the test.</summary>
    public const string OptInVariable = "HOMEBLAZE_RUN_CLAUDE_CLI_TESTS";

    public ClaudeCliFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Runs a real Claude CLI turn on the local subscription; set {OptInVariable}=1 to run it.";
            return;
        }

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
