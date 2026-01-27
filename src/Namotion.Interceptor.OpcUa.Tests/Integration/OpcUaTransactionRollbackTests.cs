using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for transaction rollback/cancellation behavior.
/// Complements OpcUaTransactionTests which only tests successful commits.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaTransactionRollbackTests : SharedServerTestBase
{
    public OpcUaTransactionRollbackTests(SharedOpcUaServerFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task Transaction_DisposedWithoutCommit_ServerShouldNotReceiveChanges()
    {
        var serverArea = Fixture.ServerRoot.Transactions.SingleProperty;
        var clientArea = Client!.Root!.Transactions.SingleProperty;

        // First, set a known value via committed transaction (client→server)
        using (var setupTransaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "InitialValue";
            await setupTransaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "InitialValue",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should have initial value from setup transaction");

        // Start transaction, make changes, but dispose without commit
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "UncommittedValue";
            // Intentionally NOT calling CommitAsync - dispose will rollback
        }

        // Wait a bit to ensure no sync happens
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Server should still have initial value
        Assert.Equal("InitialValue", serverArea.Name);
        Logger.Log("Transaction rollback verified - server did not receive uncommitted changes");
    }

    [Fact]
    public async Task Transaction_MultipleProperties_DisposedWithoutCommit_ServerShouldNotReceiveChanges()
    {
        var serverArea = Fixture.ServerRoot.Transactions.MultiProperty;
        var clientArea = Client!.Root!.Transactions.MultiProperty;

        // First, set known values via committed transaction (client→server)
        using (var setupTransaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "SetupName";
            clientArea.Number = 100m;
            await setupTransaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "SetupName" && serverArea.Number == 100m,
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should have initial values from setup transaction");

        // Start transaction with multiple property changes, but dispose without commit
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "UncommittedName";
            clientArea.Number = 999m;
            // Intentionally NOT calling CommitAsync - dispose will rollback
        }

        // Wait a bit to ensure no sync happens
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Server should still have initial values
        Assert.Equal("SetupName", serverArea.Name);
        Assert.Equal(100m, serverArea.Number);
        Logger.Log("Transaction rollback with multiple properties verified - server did not receive uncommitted changes");
    }
}
