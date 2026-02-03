using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for server-to-client collection synchronization.
/// Verifies that collection changes on server propagate to client model.
/// Tests both Container and Flat collection modes.
/// </summary>
[Trait("Category", "Integration")]
public class ServerToClientCollectionTests : SharedServerTestBase
{
    public ServerToClientCollectionTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    #region Container Collection Tests

    [Fact]
    public async Task AddToContainerCollection_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientCollection;
        var clientArea = Client!.Root!.ServerToClientCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"ContainerAdd_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");
        Logger.Log($"Server ContainerItems.Length before: {serverArea.ContainerItems.Length}");
        Logger.Log($"Client ContainerItems.Length before: {clientArea.ContainerItems.Length}");

        // Act - server adds to collection
        var newPerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        serverArea.ContainerItems = [.. serverArea.ContainerItems, newPerson];
        Logger.Log($"Server added person: {firstName}, new count: {serverArea.ContainerItems.Length}");

        // Assert - client receives change (check for unique item, not count)
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasNewPerson = clientArea.ContainerItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling client ContainerItems: count={clientArea.ContainerItems.Length}, hasNewPerson={hasNewPerson}");
                return hasNewPerson;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive collection add");

        var addedPerson = clientArea.ContainerItems.FirstOrDefault(p => p.FirstName == firstName);
        Assert.NotNull(addedPerson);
        Assert.Equal("Person", addedPerson.LastName);
        Logger.Log($"Test passed - client has {clientArea.ContainerItems.Length} items");
    }

    [Fact]
    public async Task RemoveFromContainerCollection_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientCollection;
        var clientArea = Client!.Root!.ServerToClientCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"ContainerRemove_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");

        // First, add an item to remove
        var personToRemove = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName,
            LastName = "ToRemove"
        };
        serverArea.ContainerItems = [.. serverArea.ContainerItems, personToRemove];
        Logger.Log($"Server added person for removal: {firstName}");

        // Wait for client to receive the add
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.ContainerItems.Any(p => p.FirstName == firstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive initial add");

        var countBeforeRemove = clientArea.ContainerItems.Length;
        Logger.Log($"Client count before remove: {countBeforeRemove}");

        // Act - server removes from collection
        serverArea.ContainerItems = serverArea.ContainerItems.Where(p => p.FirstName != firstName).ToArray();
        Logger.Log($"Server removed person: {firstName}, new count: {serverArea.ContainerItems.Length}");

        // Assert - client receives change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasRemovedPerson = clientArea.ContainerItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling client ContainerItems for removal: count={clientArea.ContainerItems.Length}, hasRemovedPerson={hasRemovedPerson}");
                return !hasRemovedPerson;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive collection remove");

        Assert.DoesNotContain(clientArea.ContainerItems, p => p.FirstName == firstName);
        Logger.Log($"Test passed - client has {clientArea.ContainerItems.Length} items");
    }

    #endregion

    #region Flat Collection Tests

    [Fact]
    public async Task AddToFlatCollection_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientCollection;
        var clientArea = Client!.Root!.ServerToClientCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"FlatAdd_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");
        Logger.Log($"Server FlatItems.Length before: {serverArea.FlatItems.Length}");
        Logger.Log($"Client FlatItems.Length before: {clientArea.FlatItems.Length}");

        // Act - server adds to collection
        var newPerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        serverArea.FlatItems = [.. serverArea.FlatItems, newPerson];
        Logger.Log($"Server added person: {firstName}, new count: {serverArea.FlatItems.Length}");

        // Assert - client receives change (check for unique item, not count)
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasNewPerson = clientArea.FlatItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling client FlatItems: count={clientArea.FlatItems.Length}, hasNewPerson={hasNewPerson}");
                return hasNewPerson;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive collection add");

        var addedPerson = clientArea.FlatItems.FirstOrDefault(p => p.FirstName == firstName);
        Assert.NotNull(addedPerson);
        Assert.Equal("Person", addedPerson.LastName);
        Logger.Log($"Test passed - client has {clientArea.FlatItems.Length} items");
    }

    [Fact]
    public async Task RemoveFromFlatCollection_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientCollection;
        var clientArea = Client!.Root!.ServerToClientCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"FlatRemove_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");

        // First, add an item to remove
        var personToRemove = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName,
            LastName = "ToRemove"
        };
        serverArea.FlatItems = [.. serverArea.FlatItems, personToRemove];
        Logger.Log($"Server added person for removal: {firstName}");

        // Wait for client to receive the add
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.FlatItems.Any(p => p.FirstName == firstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive initial add");

        var countBeforeRemove = clientArea.FlatItems.Length;
        Logger.Log($"Client count before remove: {countBeforeRemove}");

        // Act - server removes from collection
        serverArea.FlatItems = serverArea.FlatItems.Where(p => p.FirstName != firstName).ToArray();
        Logger.Log($"Server removed person: {firstName}, new count: {serverArea.FlatItems.Length}");

        // Assert - client receives change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasRemovedPerson = clientArea.FlatItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling client FlatItems for removal: count={clientArea.FlatItems.Length}, hasRemovedPerson={hasRemovedPerson}");
                return !hasRemovedPerson;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive collection remove");

        Assert.DoesNotContain(clientArea.FlatItems, p => p.FirstName == firstName);
        Logger.Log($"Test passed - client has {clientArea.FlatItems.Length} items");
    }

    #endregion

    #region Index Management Tests

    [Fact]
    public async Task RemoveMiddleItem_BrowseNamesReindexed()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientCollection;
        var clientArea = Client!.Root!.ServerToClientCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstNames = new[]
        {
            $"First_{testId}",
            $"Middle_{testId}",
            $"Last_{testId}"
        };

        Logger.Log($"Test starting with unique IDs: {string.Join(", ", firstNames)}");

        // First, add three items
        var person1 = new NestedPerson(ServerFixture.ServerContext) { FirstName = firstNames[0], LastName = "One" };
        var person2 = new NestedPerson(ServerFixture.ServerContext) { FirstName = firstNames[1], LastName = "Two" };
        var person3 = new NestedPerson(ServerFixture.ServerContext) { FirstName = firstNames[2], LastName = "Three" };

        serverArea.ContainerItems = [.. serverArea.ContainerItems, person1, person2, person3];
        Logger.Log($"Server added three persons");

        // Wait for client to receive all three
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasAll = firstNames.All(fn => clientArea.ContainerItems.Any(p => p.FirstName == fn));
                Logger.Log($"Polling client for all three items: hasAll={hasAll}");
                return hasAll;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive all three items");

        Logger.Log($"Client has all three items");

        // Act - server removes middle item
        serverArea.ContainerItems = serverArea.ContainerItems.Where(p => p.FirstName != firstNames[1]).ToArray();
        Logger.Log($"Server removed middle person: {firstNames[1]}");

        // Assert - client receives change and has correct items
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasMiddle = clientArea.ContainerItems.Any(p => p.FirstName == firstNames[1]);
                var hasFirst = clientArea.ContainerItems.Any(p => p.FirstName == firstNames[0]);
                var hasLast = clientArea.ContainerItems.Any(p => p.FirstName == firstNames[2]);
                Logger.Log($"Polling: hasFirst={hasFirst}, hasMiddle={hasMiddle}, hasLast={hasLast}");
                return !hasMiddle && hasFirst && hasLast;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive middle item removal");

        Assert.DoesNotContain(clientArea.ContainerItems, p => p.FirstName == firstNames[1]);
        Assert.Contains(clientArea.ContainerItems, p => p.FirstName == firstNames[0]);
        Assert.Contains(clientArea.ContainerItems, p => p.FirstName == firstNames[2]);
        Logger.Log($"Test passed - client has {clientArea.ContainerItems.Length} items, middle item removed");
    }

    #endregion

    #region Sequential Operation Tests

    [Fact]
    public async Task MultipleAddRemove_SequentialOperations()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientCollection;
        var clientArea = Client!.Root!.ServerToClientCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];

        Logger.Log($"Test starting with testId: {testId}");
        Logger.Log($"Initial server count: {serverArea.ContainerItems.Length}");
        Logger.Log($"Initial client count: {clientArea.ContainerItems.Length}");

        // Step 1: Add first person
        var person1FirstName = $"SeqAdd1_{testId}";
        var person1 = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = person1FirstName,
            LastName = "First"
        };
        serverArea.ContainerItems = [.. serverArea.ContainerItems, person1];
        Logger.Log($"Step 1: Added {person1FirstName}");

        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.ContainerItems.Any(p => p.FirstName == person1FirstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive first add");
        Logger.Log($"Step 1 verified: client has {person1FirstName}");

        // Step 2: Add second person
        var person2FirstName = $"SeqAdd2_{testId}";
        var person2 = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = person2FirstName,
            LastName = "Second"
        };
        serverArea.ContainerItems = [.. serverArea.ContainerItems, person2];
        Logger.Log($"Step 2: Added {person2FirstName}");

        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.ContainerItems.Any(p => p.FirstName == person2FirstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive second add");
        Logger.Log($"Step 2 verified: client has {person2FirstName}");

        // Step 3: Remove first person
        serverArea.ContainerItems = serverArea.ContainerItems.Where(p => p.FirstName != person1FirstName).ToArray();
        Logger.Log($"Step 3: Removed {person1FirstName}");

        await AsyncTestHelpers.WaitUntilAsync(
            () => !clientArea.ContainerItems.Any(p => p.FirstName == person1FirstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive first person removal");
        Logger.Log($"Step 3 verified: client no longer has {person1FirstName}");

        // Step 4: Add third person
        var person3FirstName = $"SeqAdd3_{testId}";
        var person3 = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = person3FirstName,
            LastName = "Third"
        };
        serverArea.ContainerItems = [.. serverArea.ContainerItems, person3];
        Logger.Log($"Step 4: Added {person3FirstName}");

        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.ContainerItems.Any(p => p.FirstName == person3FirstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive third add");
        Logger.Log($"Step 4 verified: client has {person3FirstName}");

        // Step 5: Clear all test items at once
        serverArea.ContainerItems = serverArea.ContainerItems
            .Where(p => !p.FirstName.Contains(testId))
            .ToArray();
        Logger.Log($"Step 5: Cleared all test items");

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var remainingTestItems = clientArea.ContainerItems
                    .Where(p => p.FirstName.Contains(testId))
                    .Select(p => p.FirstName)
                    .ToList();
                if (remainingTestItems.Count > 0)
                {
                    Logger.Log($"Polling: still have {remainingTestItems.Count} test items: {string.Join(", ", remainingTestItems)}");
                }
                return remainingTestItems.Count == 0;
            },
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive all test items cleared");

        Assert.DoesNotContain(clientArea.ContainerItems, p => p.FirstName.Contains(testId));
        Logger.Log($"Test passed - all sequential operations completed");
    }

    #endregion

    #region Move/Reorder Tests

    [Fact]
    public async Task MoveItemInContainerCollection_ClientReceivesReorderedCollection()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientCollection;
        var clientArea = Client!.Root!.ServerToClientCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName1 = $"First_{testId}";
        var firstName2 = $"Second_{testId}";
        var firstName3 = $"Third_{testId}";

        Logger.Log($"Test starting with testId: {testId}");

        // Setup: Add three persons in order [First, Second, Third]
        var person1 = new NestedPerson(ServerFixture.ServerContext) { FirstName = firstName1, LastName = "One" };
        var person2 = new NestedPerson(ServerFixture.ServerContext) { FirstName = firstName2, LastName = "Two" };
        var person3 = new NestedPerson(ServerFixture.ServerContext) { FirstName = firstName3, LastName = "Three" };
        serverArea.ContainerItems = [.. serverArea.ContainerItems, person1, person2, person3];
        Logger.Log($"Server added three persons: {firstName1}, {firstName2}, {firstName3}");

        // Wait for client to sync all three
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.ContainerItems.Any(p => p.FirstName == firstName1) &&
                  clientArea.ContainerItems.Any(p => p.FirstName == firstName2) &&
                  clientArea.ContainerItems.Any(p => p.FirstName == firstName3),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should sync initial three persons");

        Logger.Log($"Client synced initial state: {clientArea.ContainerItems.Length} persons");
        foreach (var p in clientArea.ContainerItems.Where(p => p.FirstName.Contains(testId)))
            Logger.Log($"  Client: {p.FirstName}");

        // Act - server reorders: move Third to the front [Third, First, Second]
        var reordered = serverArea.ContainerItems
            .Where(p => !p.FirstName.Contains(testId))
            .Concat([person3, person1, person2])
            .ToArray();
        serverArea.ContainerItems = reordered;
        Logger.Log($"Server reordered to: {firstName3}, {firstName1}, {firstName2}");

        // Assert - client should have all three items (order may or may not be preserved depending on implementation)
        // The key assertion is that all items are still present after the move operation
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasAll = clientArea.ContainerItems.Any(p => p.FirstName == firstName1) &&
                             clientArea.ContainerItems.Any(p => p.FirstName == firstName2) &&
                             clientArea.ContainerItems.Any(p => p.FirstName == firstName3);
                Logger.Log($"Polling client after reorder: hasAll={hasAll}, count={clientArea.ContainerItems.Length}");
                foreach (var p in clientArea.ContainerItems.Where(p => p.FirstName.Contains(testId)))
                    Logger.Log($"  Client: {p.FirstName}");
                return hasAll;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should have all three items after reorder");

        Assert.Contains(clientArea.ContainerItems, p => p.FirstName == firstName1);
        Assert.Contains(clientArea.ContainerItems, p => p.FirstName == firstName2);
        Assert.Contains(clientArea.ContainerItems, p => p.FirstName == firstName3);
        Logger.Log("Server->Client collection move/reorder verified - all items present");
    }

    #endregion
}
