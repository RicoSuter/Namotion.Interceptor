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
        var result = CycleVerdict.Decide(converged: true, [expectedOnly, withUnexpected], writeDurabilityResults: []);

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
        var result = CycleVerdict.Decide(converged: true, [engine], writeDurabilityResults: []);

        // Assert
        Assert.Equal(CycleResult.Pass, result);
    }

    [Fact]
    public void WhenConvergedCycleLostAWrite_ThenCycleFails()
    {
        // Arrange
        var (engine, _) = CreateEngine("client");
        var durabilityResult = new WriteDurabilityResult("client", ["client: StringValue: lost"], DroppedWriteCount: 0);

        // Act
        var result = CycleVerdict.Decide(converged: true, [engine], [durabilityResult]);

        // Assert
        Assert.Equal(CycleResult.Fail, result);
    }

    [Fact]
    public void WhenConvergedCycleLostAWriteAfterTheSourceDroppedWrites_ThenCyclePasses()
    {
        // Arrange
        var (engine, _) = CreateEngine("client");
        var durabilityResult = new WriteDurabilityResult("client", ["client: StringValue: lost"], DroppedWriteCount: 3);

        // Act
        var result = CycleVerdict.Decide(converged: true, [engine], [durabilityResult]);

        // Assert
        Assert.True(durabilityResult.IsInconclusive);
        Assert.Equal(CycleResult.Pass, result);
    }

    [Fact]
    public void WhenConvergedCycleLostAWriteAfterTheDroppedWriteCountWasReset_ThenTheResultIsInconclusive()
    {
        // Arrange
        var (engine, _) = CreateEngine("client");
        var durabilityResult = new WriteDurabilityResult("client", ["client: StringValue: lost"], DroppedWriteCount: -4);

        // Act
        var result = CycleVerdict.Decide(converged: true, [engine], [durabilityResult]);

        // Assert: neither a failure nor a silent pass.
        Assert.True(durabilityResult.IsInconclusive);
        Assert.False(durabilityResult.IsFailure);
        Assert.Equal(CycleResult.Pass, result);
    }

    [Fact]
    public void WhenCycleDidNotConverge_ThenCycleFails()
    {
        // Arrange
        var (engine, _) = CreateEngine("client");

        // Act
        var result = CycleVerdict.Decide(converged: false, [engine], writeDurabilityResults: []);

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
