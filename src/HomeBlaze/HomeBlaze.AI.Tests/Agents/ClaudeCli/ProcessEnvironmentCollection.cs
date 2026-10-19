using Xunit;

namespace HomeBlaze.AI.Tests.Agents.ClaudeCli;

/// <summary>
/// Runs tests that change the environment variables of the test process alone, after all parallel tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";

    private ProcessEnvironmentCollection()
    {
    }
}
