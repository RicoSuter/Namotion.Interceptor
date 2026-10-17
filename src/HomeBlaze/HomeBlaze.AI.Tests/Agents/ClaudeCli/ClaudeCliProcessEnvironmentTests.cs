using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.ClaudeCli;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.ClaudeCli;

[Collection(ProcessEnvironmentCollection.Name)]
public class ClaudeCliProcessEnvironmentTests
{
    private static readonly string[] RemovedVariables =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_UNIX_SOCKET",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY",
        "CLAUDE_CODE_USE_ANTHROPIC_AWS",
        "CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD",
        "CLAUDE_CODE_USE_MANTLE",
        "CLAUDE_CODE_USE_GATEWAY",
        "CLAUDE_CODE_API_BASE_URL",
        "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR",
        "CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST",
        "CLAUDE_CODE_HOST_AUTH_ENV_VAR",
        "CLAUDE_CODE_HOST_CREDS_FILE"
    ];

    private const string KeptVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    [UnixFact]
    public async Task WhenTheProcessHasApiCredentials_ThenTheCliRunsWithoutThemButKeepsTheSubscriptionToken()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");
        var previousValues = RemovedVariables.Append(KeptVariable).ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in RemovedVariables)
            {
                Environment.SetEnvironmentVariable(name, "set-by-test");
            }

            Environment.SetEnvironmentVariable(KeptVariable, "subscription-token");

            // Act
            var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None);

            // Assert
            Assert.Equal(0, result.ExitCode);
            var environment = cli.LastEnvironment;
            foreach (var name in RemovedVariables)
            {
                Assert.DoesNotContain(environment, line => line.StartsWith(name + "=", StringComparison.Ordinal));
            }

            Assert.Contains(KeptVariable + "=subscription-token", environment);
        }
        finally
        {
            foreach (var (name, value) in previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [UnixFact]
    public async Task WhenTheProcessPointsToAnAnthropicConfigDirectory_ThenTheCliGetsAnEmptyOwnerOnlyOneOfTheAgentInstead()
    {
        // Arrange - a profile left in the folder of the agent by an earlier run must not apply.
        using var cli = FakeClaudeCli.Create("success.jsonl");
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");
        var leftoverDirectory = Directory.CreateDirectory(Path.Combine(cli.Directory, ".anthropic-config", "configs"));
        await File.WriteAllTextAsync(Path.Combine(leftoverDirectory.FullName, "default.json"), "{}");
        var previousValue = Environment.GetEnvironmentVariable("ANTHROPIC_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_CONFIG_DIR", "/set-by-test");

            // Act
            var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None);

            // Assert
            Assert.Equal(0, result.ExitCode);
            var configDirectory = Assert.Single(
                cli.LastEnvironment, line => line.StartsWith("ANTHROPIC_CONFIG_DIR=", StringComparison.Ordinal))["ANTHROPIC_CONFIG_DIR=".Length..];
            Assert.Equal(cli.Directory, Path.GetDirectoryName(configDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(configDirectory));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(configDirectory));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_CONFIG_DIR", previousValue);
        }
    }

    [UnixFact]
    public async Task WhenTheProcessHasAProxyExclusionList_ThenTheCliGetsItWithTheLoopbackAddressInBothSpellings()
    {
        // Arrange - with HTTP_PROXY set, the CLI would otherwise send the calls to the agent route and its token through the proxy.
        using var cli = FakeClaudeCli.Create("success.jsonl");
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");
        var previousUpperCase = Environment.GetEnvironmentVariable("NO_PROXY");
        var previousLowerCase = Environment.GetEnvironmentVariable("no_proxy");
        try
        {
            Environment.SetEnvironmentVariable("NO_PROXY", "nas.example");
            Environment.SetEnvironmentVariable("no_proxy", null);

            // Act
            var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None);

            // Assert
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("NO_PROXY=nas.example,127.0.0.1", cli.LastEnvironment);
            Assert.Contains("no_proxy=nas.example,127.0.0.1", cli.LastEnvironment);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_PROXY", previousUpperCase);
            Environment.SetEnvironmentVariable("no_proxy", previousLowerCase);
        }
    }

    [Theory]
    [InlineData(null, "127.0.0.1")]
    [InlineData("", "127.0.0.1")]
    [InlineData("nas.example", "nas.example,127.0.0.1")]
    [InlineData("nas.example, 127.0.0.1", "nas.example, 127.0.0.1")]
    [InlineData("127.0.0.1,nas.example", "127.0.0.1,nas.example")]
    public void WhenAddingTheLoopbackAddressToAProxyExclusionList_ThenExistingEntriesAreKeptAndItIsAddedOnce(string? value, string expected)
    {
        // Act
        var result = ClaudeCliProcess.WithLoopbackProxyExclusion(value);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("ANTHROPIC_API_KEY", StringComparison.Ordinal, true)]
    [InlineData("ANTHROPIC_SOME_FUTURE_PROVIDER", StringComparison.Ordinal, true)]
    [InlineData("CLAUDE_CODE_USE_GATEWAY", StringComparison.Ordinal, true)]
    [InlineData("CLAUDE_CODE_API_BASE_URL", StringComparison.Ordinal, true)]
    [InlineData("CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR", StringComparison.Ordinal, true)]
    [InlineData("CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST", StringComparison.Ordinal, true)]
    [InlineData("CLAUDE_CODE_HOST_AUTH_ENV_VAR", StringComparison.Ordinal, true)]
    [InlineData("claude_code_host_creds_file", StringComparison.OrdinalIgnoreCase, true)]
    [InlineData("CLAUDE_CODE_API_BASE_URL_SUFFIX", StringComparison.Ordinal, false)]
    [InlineData("claude_code_api_base_url", StringComparison.Ordinal, false)]
    [InlineData("claude_code_api_base_url", StringComparison.OrdinalIgnoreCase, true)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", StringComparison.Ordinal, false)]
    [InlineData("PATH", StringComparison.Ordinal, false)]
    [InlineData("anthropic_api_key", StringComparison.Ordinal, false)]
    [InlineData("anthropic_api_key", StringComparison.OrdinalIgnoreCase, true)]
    [InlineData("Claude_Code_Use_Bedrock", StringComparison.OrdinalIgnoreCase, true)]
    [InlineData("claude_code_oauth_token", StringComparison.OrdinalIgnoreCase, false)]
    public void WhenCheckingAVariableName_ThenOnlyProviderAndApiVariablesAreRemovedInTheComparisonOfThePlatform(
        string name, StringComparison comparison, bool expected)
    {
        // Act
        var isRemoved = ClaudeCliProcess.IsRemovedEnvironmentVariable(name, comparison);

        // Assert
        Assert.Equal(expected, isRemoved);
    }
}
