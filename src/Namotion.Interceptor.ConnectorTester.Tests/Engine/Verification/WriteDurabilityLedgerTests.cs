using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
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
        ledger.Record(node, property: 0, "written-value");
        node.StringValue = "server-value";

        // Act
        var violations = ledger.Verify([node]);

        // Assert
        var violation = Assert.Single(violations);
        Assert.Equal("Collection[0].StringValue: wrote 'written-value', model holds 'server-value'", violation);
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
        ledger.Record(node, property, TestNode.WriteValueProperty(node, property, counter: 42));

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
        ledger.Record(node, property, TestNode.WriteValueProperty(node, property, counter: 42));
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
    public void WhenNodeIsNotReachable_ThenVerifySkipsIt()
    {
        // Arrange
        var node = CreateCollectionChild();
        var ledger = new WriteDurabilityLedger();
        ledger.Record(node, property: 0, "written-value");
        node.StringValue = "server-value";

        // Act
        var violations = ledger.Verify([]);

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void WhenCommitSucceeded_ThenRecordCommitRecordsEveryWrite()
    {
        // Arrange
        var node = CreateCollectionChild();
        var ledger = new WriteDurabilityLedger();
        node.StringValue = "server-value";

        // Act
        ledger.RecordCommit([(node, 0, "written-value")], failure: null);

        // Assert
        Assert.Single(ledger.Verify([node]));
    }

    [Fact]
    public void WhenCommitFailed_ThenRecordCommitRecordsOnlyTheAppliedWrites()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = TestNode.CreateWithGraph(context, collectionCount: 2, dictionaryCount: 0);
        var appliedNode = root.Collection[0];
        var failedNode = root.Collection[1];
        appliedNode.StringValue = "applied-value";
        failedNode.StringValue = "previous-value";
        var failure = new SubjectTransactionException(
            "Partial failure.",
            [CreateStringValueChange(appliedNode, "applied-value")],
            [CreateStringValueChange(failedNode, "failed-value")],
            [new InvalidOperationException("Source is not connected")]);
        var ledger = new WriteDurabilityLedger();

        // Act
        ledger.RecordCommit([(appliedNode, 0, "applied-value"), (failedNode, 0, "failed-value")], failure);

        // Assert
        Assert.Empty(ledger.Verify([appliedNode, failedNode]));
        appliedNode.StringValue = "lost";
        var violation = Assert.Single(ledger.Verify([appliedNode, failedNode]));
        Assert.StartsWith("Collection[0].StringValue: ", violation);
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
