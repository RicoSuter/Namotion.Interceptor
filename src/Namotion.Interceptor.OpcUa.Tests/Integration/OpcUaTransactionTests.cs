using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaTransactionTests : SharedServerTestBase
{
    public OpcUaTransactionTests(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
        : base(serverFixture, clientFixture, output) { }

    [Fact]
    public async Task Transaction_CommitSingleProperty_ServerReceivesChangeOnlyAfterCommit()
    {
        var serverArea = ServerFixture.ServerRoot.Transactions.SingleProperty;
        var clientArea = Client!.Root!.Transactions.SingleProperty;
        var initialName = serverArea.Name;

        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "Transaction Value";

            // Server should still have old value before commit
            Assert.Equal(initialName, serverArea.Name);

            await transaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Transaction Value",
            timeout: TimeSpan.FromSeconds(60),
            message: "Server should receive committed transaction value");
    }

    [Fact]
    public async Task Transaction_CommitMultipleProperties_ServerReceivesAllChangesOnlyAfterCommit()
    {
        var serverArea = ServerFixture.ServerRoot.Transactions.MultiProperty;
        var clientArea = Client!.Root!.Transactions.MultiProperty;
        var initialName = serverArea.Name;
        var initialNumber = serverArea.Number;

        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "Multi-Property Test";
            clientArea.Number = 123.45m;

            // Server should still have old values before commit
            Assert.Equal(initialName, serverArea.Name);
            Assert.Equal(initialNumber, serverArea.Number);

            await transaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Multi-Property Test" && serverArea.Number == 123.45m,
            timeout: TimeSpan.FromSeconds(60),
            message: "Server should receive all committed transaction values");

        Assert.Equal("Multi-Property Test", serverArea.Name);
        Assert.Equal(123.45m, serverArea.Number);
    }

    [Fact]
    public async Task Transaction_DisposedWithoutCommit_ServerShouldNotReceiveChanges()
    {
        var serverArea = ServerFixture.ServerRoot.Transactions.SingleProperty;
        var clientArea = Client!.Root!.Transactions.SingleProperty;

        // First, set a known value via committed transaction
        using (var setupTransaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "InitialValue";
            await setupTransaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "InitialValue",
            timeout: TimeSpan.FromSeconds(60),
            message: "Server should have initial value from setup transaction");

        // Start transaction, make changes, but dispose without commit
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "UncommittedValue";
            // Intentionally NOT calling CommitAsync - dispose will rollback
        }

        // Wait to ensure no sync happens
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Server should still have initial value
        Assert.Equal("InitialValue", serverArea.Name);
        Logger.Log("Transaction rollback verified - server did not receive uncommitted changes");
    }

    [Fact]
    public async Task Transaction_MultipleProperties_DisposedWithoutCommit_ServerShouldNotReceiveChanges()
    {
        var serverArea = ServerFixture.ServerRoot.Transactions.MultiProperty;
        var clientArea = Client!.Root!.Transactions.MultiProperty;

        // First, set known values via committed transaction
        using (var setupTransaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "SetupName";
            clientArea.Number = 100m;
            await setupTransaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "SetupName" && serverArea.Number == 100m,
            timeout: TimeSpan.FromSeconds(60),
            message: "Server should have initial values from setup transaction");

        // Start transaction with multiple property changes, but dispose without commit
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "UncommittedName";
            clientArea.Number = 999m;
            // Intentionally NOT calling CommitAsync - dispose will rollback
        }

        // Wait to ensure no sync happens
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Server should still have initial values
        Assert.Equal("SetupName", serverArea.Name);
        Assert.Equal(100m, serverArea.Number);
        Logger.Log("Transaction rollback with multiple properties verified");
    }
}
