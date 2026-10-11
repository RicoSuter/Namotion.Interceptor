using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

[Collection(ProcessEnvironmentCollection.Name)]
public class ClaudeCliProcessEnvironmentTests
{
    private static readonly string[] RemovedVariables =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY",
        "ANTHROPIC_PROFILE",
        "ANTHROPIC_FEDERATION_RULE_ID",
        "ANTHROPIC_ORGANIZATION_ID"
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
            var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), CancellationToken.None);

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
}
