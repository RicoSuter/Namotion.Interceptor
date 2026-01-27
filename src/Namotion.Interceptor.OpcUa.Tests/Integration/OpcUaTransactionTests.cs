using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaTransactionTests : SharedServerTestBase
{
    public OpcUaTransactionTests(SharedOpcUaServerFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task Transaction_CommitSingleProperty_ServerReceivesChangeOnlyAfterCommit()
    {
        // Arrange - use dedicated test area
        var serverArea = Fixture.ServerRoot.Transactions.SingleProperty;
        var clientArea = Client!.Root!.Transactions.SingleProperty;
        var initialName = serverArea.Name;

        // Act
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "Transaction Value";

            // Assert - server should still have old value before commit
            Assert.Equal(initialName, serverArea.Name);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Wait for OPC UA sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Transaction Value",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive committed transaction value");
    }

    [Fact]
    public async Task Transaction_CommitMultipleProperties_ServerReceivesAllChangesOnlyAfterCommit()
    {
        // Arrange - use dedicated test area
        var serverArea = Fixture.ServerRoot.Transactions.MultiProperty;
        var clientArea = Client!.Root!.Transactions.MultiProperty;
        var initialName = serverArea.Name;
        var initialNumber = serverArea.Number;

        // Act
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "Multi-Property Test";
            clientArea.Number = 123.45m;

            // Assert - server should still have old values before commit
            Assert.Equal(initialName, serverArea.Name);
            Assert.Equal(initialNumber, serverArea.Number);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Wait for OPC UA sync of all properties
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Multi-Property Test" && serverArea.Number == 123.45m,
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive all committed transaction values");

        // Assert - server should now have all new values after commit
        Assert.Equal("Multi-Property Test", serverArea.Name);
        Assert.Equal(123.45m, serverArea.Number);
    }
}
