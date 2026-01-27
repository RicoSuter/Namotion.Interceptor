using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for client-initiated nested property changes.
/// Tests client→server sync for nested structures via transactions.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaBidirectionalNestedTests : SharedServerTestBase
{
    public OpcUaBidirectionalNestedTests(SharedOpcUaServerFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task NestedPropertyChanges_ClientToServer_ShouldReachServer()
    {
        var serverArea = Fixture.ServerRoot.Nested;
        var clientArea = Client!.Root!.Nested;

        // Test 1: Object reference property (client → server)
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Person.FirstName = "ClientFirst";
            await transaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Person.FirstName == "ClientFirst",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive client's Person.FirstName update");

        // Test 2: Deep nesting (client → server)
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Person.Address!.City = "ClientCity";
            clientArea.Person.Address!.ZipCode = "99999";
            await transaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Person.Address!.City == "ClientCity" &&
                  serverArea.Person.Address!.ZipCode == "99999",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive client's deep nested address update");

        // Test 3: Collection item property (client → server)
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.People[0].LastName = "ClientLast";
            await transaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.People[0].LastName == "ClientLast",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive client's People[0].LastName update");

        // Test 4: Dictionary item property (client → server)
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.PeopleByName!["john"].FirstName = "ClientJohn";
            await transaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.PeopleByName!["john"].FirstName == "ClientJohn",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive client's dictionary item update");

        // Test 5: OpcUaValue pattern (client → server)
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Sensor!.Value = 99.9;
            await transaction.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => Math.Abs(serverArea.Sensor!.Value - 99.9) < 0.01,
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive client's Sensor.Value update");

        Logger.Log("All client→server nested property tests passed!");
    }
}
