using Xunit;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class WriteDurabilityLedgerTests
{
    private static TestNode CreateCollectionChild()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = TestNode.CreateWithGraph(context, collectionCount: 1, dictionaryCount: 0);
        return root.Collection[0];
    }

    [Fact]
    public void WhenModelDivergesFromRecordedWrite_ThenVerifyReportsThePropertyPathAndBothValues()
    {
        // Arrange
        var node = CreateCollectionChild();
        var ledger = new WriteDurabilityLedger();
        ledger.Record(node, property: 0, counter: 42);
        node.StringValue = "server-value";

        // Act
        var violations = ledger.Verify([node]);

        // Assert
        var violation = Assert.Single(violations);
        Assert.Equal("Collection[0].StringValue: wrote '0000002a', model holds 'server-value'", violation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WhenModelHoldsTheRecordedWrite_ThenVerifyReportsNothing(int property)
    {
        // Arrange
        var node = CreateCollectionChild();
        var ledger = new WriteDurabilityLedger();
        TestNode.WriteValueProperty(node, property, counter: 42);
        ledger.Record(node, property, counter: 42);

        // Act
        var violations = ledger.Verify([node]);

        // Assert
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData(0, nameof(TestNode.StringValue))]
    [InlineData(1, nameof(TestNode.DecimalValue))]
    [InlineData(2, nameof(TestNode.IntValue))]
    [InlineData(3, nameof(TestNode.LongValue))]
    public void WhenModelLostTheRecordedWrite_ThenVerifyNamesThePropertyAtTheRecordedIndex(int property, string expectedProperty)
    {
        // Arrange
        var node = CreateCollectionChild();
        var ledger = new WriteDurabilityLedger();
        TestNode.WriteValueProperty(node, property, counter: 42);
        ledger.Record(node, property, counter: 42);
        node.StringValue = "lost";
        node.DecimalValue = -1;
        node.IntValue = -1;
        node.LongValue = -1;

        // Act
        var violations = ledger.Verify([node]);

        // Assert
        var violation = Assert.Single(violations);
        Assert.StartsWith($"Collection[0].{expectedProperty}: wrote ", violation);
    }

    [Fact]
    public void WhenNodeIsDetachedFromTheModel_ThenVerifyReportsIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = TestNode.CreateWithGraph(context, collectionCount: 1, dictionaryCount: 0);
        var node = root.Collection[0];
        var ledger = new WriteDurabilityLedger();
        TestNode.WriteValueProperty(node, property: 0, counter: 42);
        ledger.Record(node, property: 0, counter: 42);
        root.Collection = [];

        // Act
        var violations = ledger.Verify([root]);

        // Assert
        var violation = Assert.Single(violations);
        Assert.Equal("StringValue: no longer in the model", violation);
    }

    [Fact]
    public void WhenCommitSucceeded_ThenRecordCommitRecordsEveryWrite()
    {
        // Arrange
        var node = CreateCollectionChild();
        var ledger = new WriteDurabilityLedger();
        node.StringValue = "server-value";

        // Act
        ledger.RecordCommit([(node, 0, 42)], failure: null);

        // Assert
        Assert.Single(ledger.Verify([node]));
    }

    [Fact]
    public void WhenCommitFailed_ThenRecordCommitDoesNotRecordTheFailedWrites()
    {
        // Arrange
        var (appliedNode, failedNode, failure) = CreatePartiallyFailedCommit();
        var ledger = new WriteDurabilityLedger();

        // Act
        ledger.RecordCommit([(appliedNode, 0, 42), (failedNode, 0, 43)], failure);
        var violations = ledger.Verify([appliedNode, failedNode]);

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void WhenCommitFailed_ThenRecordCommitRecordsTheAppliedWrites()
    {
        // Arrange
        var (appliedNode, failedNode, failure) = CreatePartiallyFailedCommit();
        appliedNode.StringValue = "lost";
        var ledger = new WriteDurabilityLedger();

        // Act
        ledger.RecordCommit([(appliedNode, 0, 42), (failedNode, 0, 43)], failure);
        var violations = ledger.Verify([appliedNode, failedNode]);

        // Assert
        var violation = Assert.Single(violations);
        Assert.StartsWith("Collection[0].StringValue: ", violation);
    }

    [Fact]
    public void WhenTheSourceDropsWrites_ThenDroppedWriteCountCountsOnlyThoseSinceTheLastReset()
    {
        // Arrange
        var source = new FailingSource { RootSubject = new TestNode(InterceptorSubjectContext.Create()) };
        var ledger = new WriteDurabilityLedger(source);
        source.Metrics.OutboundRetries.AddDropped(10);
        ledger.Reset();
        source.Metrics.OutboundRetries.AddDropped(3);

        // Act
        var droppedWriteCount = ledger.DroppedWriteCount;

        // Assert
        Assert.Equal(3, droppedWriteCount);
    }

    [Fact]
    public void WhenTheParticipantHasNoSource_ThenDroppedWriteCountIsZero()
    {
        // Arrange
        var ledger = new WriteDurabilityLedger();
        ledger.Reset();

        // Act
        var droppedWriteCount = ledger.DroppedWriteCount;

        // Assert
        Assert.Equal(0, droppedWriteCount);
    }

    [Theory]
    [InlineData(SourceState.Synchronizing, false)]
    [InlineData(SourceState.Synchronized, true)]
    public void WhenTheSourceHasAState_ThenIsSourceSynchronizedReflectsIt(SourceState state, bool expected)
    {
        // Arrange
        var source = new FailingSource { RootSubject = new TestNode(InterceptorSubjectContext.Create()), State = state };
        var ledger = new WriteDurabilityLedger(source);

        // Act
        var isSourceSynchronized = ledger.IsSourceSynchronized;

        // Assert
        Assert.Equal(expected, isSourceSynchronized);
    }

    [Fact]
    public void WhenTheParticipantHasNoSource_ThenIsSourceSynchronized()
    {
        // Arrange
        var ledger = new WriteDurabilityLedger();

        // Act
        var isSourceSynchronized = ledger.IsSourceSynchronized;

        // Assert
        Assert.True(isSourceSynchronized);
    }

    /// <summary>
    /// Creates a failed commit of counter 42 to the first node's string value, which it applied, and of counter 43 to
    /// the second node's, which it did not.
    /// </summary>
    private static (TestNode AppliedNode, TestNode FailedNode, SubjectTransactionException Failure) CreatePartiallyFailedCommit()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = TestNode.CreateWithGraph(context, collectionCount: 2, dictionaryCount: 0);
        var appliedNode = root.Collection[0];
        var failedNode = root.Collection[1];
        TestNode.WriteValueProperty(appliedNode, property: 0, counter: 42);
        failedNode.StringValue = "previous-value";
        var failure = new SubjectTransactionException(
            "Partial failure.",
            [CreateStringValueChange(appliedNode, appliedNode.StringValue)],
            [CreateStringValueChange(failedNode, "0000002b")],
            [new InvalidOperationException("Source is not connected")]);
        return (appliedNode, failedNode, failure);
    }

    private static SubjectPropertyChange CreateStringValueChange(TestNode node, string value)
        => SubjectPropertyChange.Create(
            new PropertyReference(node, nameof(TestNode.StringValue)),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            string.Empty,
            value);
}
