using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class WriteDurabilityLedgerTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();

    [Fact]
    public void WhenModelDivergesFromRecordedWrite_ThenVerifyReturnsOneViolationNamingProperty()
    {
        // Arrange
        var context = CreateContext();
        var node = new TestNode(context);
        var ledger = new WriteDurabilityLedger();
        ledger.Record(node, property: 0, "written-value");

        // Act: mutate the node behind the ledger's back, as a lost write's revert would.
        node.StringValue = "server-value";
        var violations = ledger.Verify([node]);

        // Assert
        var violation = Assert.Single(violations);
        Assert.Contains("property 0", violation);
    }

    [Fact]
    public void WhenModelStillHoldsRecordedWrite_ThenVerifyReturnsNoViolations()
    {
        // Arrange
        var context = CreateContext();
        var node = new TestNode(context);
        var ledger = new WriteDurabilityLedger();
        node.StringValue = "written-value";
        ledger.Record(node, property: 0, "written-value");

        // Act
        var violations = ledger.Verify([node]);

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void WhenNodeNotReachable_ThenVerifySkipsIt()
    {
        // Arrange
        var context = CreateContext();
        var node = new TestNode(context);
        var ledger = new WriteDurabilityLedger();
        ledger.Record(node, property: 0, "written-value");
        node.StringValue = "server-value";

        // Act: node is absent from the reachable set, as if the run removed it structurally.
        var violations = ledger.Verify([]);

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void WhenPropertyForgotten_ThenVerifyIgnoresIt()
    {
        // Arrange
        var context = CreateContext();
        var node = new TestNode(context);
        var ledger = new WriteDurabilityLedger();
        ledger.Record(node, property: 0, "written-value");
        ledger.Forget(node, property: 0);
        node.StringValue = "server-value";

        // Act
        var violations = ledger.Verify([node]);

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void WhenReset_ThenAllRecordedWritesAreCleared()
    {
        // Arrange
        var context = CreateContext();
        var node = new TestNode(context);
        var ledger = new WriteDurabilityLedger();
        ledger.Record(node, property: 0, "written-value");
        node.StringValue = "server-value";

        // Act
        ledger.Reset();
        var violations = ledger.Verify([node]);

        // Assert
        Assert.Empty(violations);
    }
}
