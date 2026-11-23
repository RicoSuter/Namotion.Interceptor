using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class CycleVerdictTests
{
    [Fact]
    public void WhenConvergedCycleHadAnUnexpectedCommitFailure_ThenCycleFails()
    {
        // Arrange
        var (expectedOnly, expectedCounters) = CreateEngine("server");
        var (withUnexpected, unexpectedCounters) = CreateEngine("client");
        expectedCounters.IncrementFailedCommit(CommitFailureKind.SourceWriteFailure);
        unexpectedCounters.IncrementFailedCommit(CommitFailureKind.Unexpected);

        // Act
        var result = CycleVerdict.Decide(converged: true, [expectedOnly, withUnexpected]);

        // Assert
        Assert.Equal(CycleResult.Fail, result);
    }

    [Fact]
    public void WhenConvergedCycleHadOnlyExpectedCommitFailures_ThenCyclePasses()
    {
        // Arrange
        var (engine, counters) = CreateEngine("client");
        counters.IncrementFailedCommit(CommitFailureKind.Conflict);
        counters.IncrementFailedCommit(CommitFailureKind.SourceWriteFailure);

        // Act
        var result = CycleVerdict.Decide(converged: true, [engine]);

        // Assert
        Assert.Equal(CycleResult.Pass, result);
    }

    [Fact]
    public void WhenCycleDidNotConverge_ThenCycleFails()
    {
        // Arrange
        var (engine, _) = CreateEngine("client");

        // Act
        var result = CycleVerdict.Decide(converged: false, [engine]);

        // Assert
        Assert.Equal(CycleResult.Fail, result);
    }

    private static (MutationEngine Engine, MutationCounters Counters) CreateEngine(string name)
    {
        var context = InterceptorSubjectContext.Create();
        var root = new TestNode(context);
        var graph = new KnownNodeGraph();
        var counters = new MutationCounters();
        var coordinator = new TestCycleCoordinator();
        var configuration = new ParticipantConfiguration { Name = name, ValueMutationRate = 1 };
        var strategy = new RandomValueMutationStrategy(graph, coordinator, context, counters, configuration, NullLogger.Instance);
        return (new MutationEngine(root, configuration, coordinator, strategy, graph, counters, NullLogger.Instance), counters);
    }
}
