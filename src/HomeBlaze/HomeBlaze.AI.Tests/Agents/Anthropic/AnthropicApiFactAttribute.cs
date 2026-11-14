using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Anthropic;

/// <summary>
/// A fact that runs only when <see cref="OptInVariable"/> is set to 1 and <see cref="ApiKeyVariable"/> holds an API
/// key, because it spends API credits.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AnthropicApiFactAttribute : FactAttribute
{
    /// <summary>The environment variable that opts in to running the test.</summary>
    public const string OptInVariable = "HOMEBLAZE_RUN_ANTHROPIC_API_TESTS";

    /// <summary>The environment variable that holds the Anthropic API key.</summary>
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    public AnthropicApiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Runs a real Anthropic API turn that spends API credits; set {OptInVariable}=1 and {ApiKeyVariable} to run it.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable)))
        {
            Skip = $"{ApiKeyVariable} is not set.";
        }
    }
}
