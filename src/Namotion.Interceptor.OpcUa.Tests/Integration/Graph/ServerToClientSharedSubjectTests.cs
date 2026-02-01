using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for shared subject scenarios where the same object is referenced by multiple parents (collections).
/// Verifies that ReferenceAdded and ReferenceDeleted events properly update collections on the client side.
/// </summary>
[Trait("Category", "Integration")]
public class ServerToClientSharedSubjectTests : SharedServerTestBase
{
    public ServerToClientSharedSubjectTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    /// <summary>
    /// Tests that when a server adds a shared subject to a second collection,
    /// the client receives a ReferenceAdded event and sees the subject in both collections.
    /// </summary>
    [Fact]
    public async Task AddToSecondCollection_ClientReceivesReferenceAdded()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientSharedSubject;
        var clientArea = Client!.Root!.ServerToClientSharedSubject;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"Shared_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");
        Logger.Log($"Server PrimaryItems.Length: {serverArea.PrimaryItems.Length}");
        Logger.Log($"Server SecondaryItems.Length: {serverArea.SecondaryItems.Length}");

        // Act Step 1: Add subject to PrimaryItems on server
        var sharedPerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        serverArea.PrimaryItems = [..serverArea.PrimaryItems, sharedPerson];
        Logger.Log($"Server: Added shared person to PrimaryItems");

        // Wait for client to sync PrimaryItems
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var match = clientArea.PrimaryItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling client PrimaryItems for {firstName}: found={match}, count={clientArea.PrimaryItems.Length}");
                return match;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive person in PrimaryItems");

        var primaryItem = clientArea.PrimaryItems.First(p => p.FirstName == firstName);
        Assert.Equal("Person", primaryItem.LastName);
        Logger.Log($"Client: Found person in PrimaryItems with FirstName={firstName}");

        // Act Step 2: Add SAME subject to SecondaryItems on server
        // This should trigger a ReferenceAdded event (not NodeAdded, since node already exists)
        serverArea.SecondaryItems = [..serverArea.SecondaryItems, sharedPerson];
        Logger.Log("Server: Added same shared person to SecondaryItems");

        // Assert: Client should see the person in BOTH collections
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var match = clientArea.SecondaryItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling client SecondaryItems for {firstName}: found={match}, count={clientArea.SecondaryItems.Length}");
                return match;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive person in SecondaryItems via ReferenceAdded");

        // Verify both collections have the person
        Assert.Contains(clientArea.PrimaryItems, p => p.FirstName == firstName);
        Assert.Contains(clientArea.SecondaryItems, p => p.FirstName == firstName);
        Logger.Log($"Verified: Client sees shared person in both collections");

        // Cleanup: remove test items from both collections
        serverArea.PrimaryItems = serverArea.PrimaryItems.Where(p => p.FirstName != firstName).ToArray();
        serverArea.SecondaryItems = serverArea.SecondaryItems.Where(p => p.FirstName != firstName).ToArray();
        Logger.Log("Cleanup: Removed test items from server collections");
    }

    /// <summary>
    /// Tests that when a server removes a shared subject from one collection (but keeps it in another),
    /// the client receives a ReferenceDeleted event and sees the subject only in the remaining collection.
    /// </summary>
    [Fact]
    public async Task RemoveFromOneCollection_ClientReceivesReferenceDeleted()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientSharedSubject;
        var clientArea = Client!.Root!.ServerToClientSharedSubject;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"Remove_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");

        // Step 1: Add shared person to PrimaryItems on server
        var sharedPerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        serverArea.PrimaryItems = [..serverArea.PrimaryItems, sharedPerson];
        Logger.Log("Server: Added shared person to PrimaryItems");

        // Wait for client to sync PrimaryItems
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.PrimaryItems.Any(p => p.FirstName == firstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive person in PrimaryItems");

        Logger.Log($"Client: PrimaryItems count={clientArea.PrimaryItems.Length}");

        // Step 2: Add SAME person to SecondaryItems on server
        serverArea.SecondaryItems = [..serverArea.SecondaryItems, sharedPerson];
        Logger.Log("Server: Added same shared person to SecondaryItems");

        // Wait for client to sync SecondaryItems via ReferenceAdded
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.SecondaryItems.Any(p => p.FirstName == firstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive person in SecondaryItems via ReferenceAdded");

        Logger.Log($"Client: SecondaryItems count={clientArea.SecondaryItems.Length}");
        Assert.Contains(clientArea.PrimaryItems, p => p.FirstName == firstName);
        Assert.Contains(clientArea.SecondaryItems, p => p.FirstName == firstName);
        Logger.Log("Verified: Client sees shared person in both collections");

        // Step 3: Remove person from SecondaryItems on server (keep in PrimaryItems)
        // This should trigger a ReferenceDeleted event (not NodeDeleted, since node still exists in PrimaryItems)
        serverArea.SecondaryItems = serverArea.SecondaryItems.Where(p => p.FirstName != firstName).ToArray();
        Logger.Log("Server: Removed shared person from SecondaryItems (kept in PrimaryItems)");

        // Assert: Client should see person ONLY in PrimaryItems, not in SecondaryItems
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var inSecondary = clientArea.SecondaryItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling client SecondaryItems: person found={inSecondary}");
                return !inSecondary;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive ReferenceDeleted and remove person from SecondaryItems");

        // Verify PrimaryItems still has the person
        Assert.Contains(clientArea.PrimaryItems, p => p.FirstName == firstName);
        // Verify SecondaryItems no longer has the person
        Assert.DoesNotContain(clientArea.SecondaryItems, p => p.FirstName == firstName);
        Logger.Log("Verified: Client sees shared person only in PrimaryItems after removal from SecondaryItems");

        // Cleanup: remove test items from PrimaryItems
        serverArea.PrimaryItems = serverArea.PrimaryItems.Where(p => p.FirstName != firstName).ToArray();
        Logger.Log("Cleanup: Removed test items from server collections");
    }

    /// <summary>
    /// Tests that when a server removes a shared subject from one parent collection,
    /// the server sends ReferenceDeleted (NOT NodeDeleted), so the node still exists in the other collection.
    /// </summary>
    [Fact]
    public async Task RemoveFromOneParent_NodeStillExists()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientSharedSubject;
        var clientArea = Client!.Root!.ServerToClientSharedSubject;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"NodeExists_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");

        // Step 1: Add person to PrimaryItems on server
        var sharedPerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        serverArea.PrimaryItems = [..serverArea.PrimaryItems, sharedPerson];
        Logger.Log("Server: Added shared person to PrimaryItems");

        // Wait for client to sync PrimaryItems
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.PrimaryItems.Any(p => p.FirstName == firstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should sync person from PrimaryItems");

        Logger.Log($"Client: PrimaryItems count={clientArea.PrimaryItems.Length}");

        // Step 2: Add shared person to SecondaryItems on server
        serverArea.SecondaryItems = [..serverArea.SecondaryItems, sharedPerson];
        Logger.Log("Server: Added shared person to SecondaryItems");

        // Wait for client to sync SecondaryItems via ReferenceAdded
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.SecondaryItems.Any(p => p.FirstName == firstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive person in SecondaryItems via ReferenceAdded");

        Assert.Contains(clientArea.PrimaryItems, p => p.FirstName == firstName);
        Assert.Contains(clientArea.SecondaryItems, p => p.FirstName == firstName);
        Logger.Log("Verified: Client sees shared person in both collections");

        // Step 3: Remove person from PrimaryItems on server (keep in SecondaryItems)
        // This should trigger ReferenceDeleted (NOT NodeDeleted)
        serverArea.PrimaryItems = serverArea.PrimaryItems.Where(p => p.FirstName != firstName).ToArray();
        Logger.Log("Server: Removed shared person from PrimaryItems (kept in SecondaryItems)");

        // Assert: Client should see person ONLY in SecondaryItems
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var inPrimary = clientArea.PrimaryItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling client PrimaryItems: person found={inPrimary}");
                return !inPrimary;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive ReferenceDeleted and remove person from PrimaryItems");

        // CRITICAL: Verify SecondaryItems STILL has the person (node was NOT deleted)
        // If NodeDeleted was sent instead of ReferenceDeleted, the client would have removed
        // the subject entirely, and SecondaryItems would also be empty or have a stale reference
        Assert.DoesNotContain(clientArea.PrimaryItems, p => p.FirstName == firstName);
        Assert.Contains(clientArea.SecondaryItems, p => p.FirstName == firstName);
        Logger.Log("Verified: Person still exists in SecondaryItems (ReferenceDeleted sent, not NodeDeleted)");

        // Step 4: Update value on server - client should still receive updates
        // This proves the node is still alive and the subscription is working
        sharedPerson.LastName = "UpdatedPerson";
        Logger.Log("Server: Updated shared person lastName to 'UpdatedPerson'");

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var person = clientArea.SecondaryItems.FirstOrDefault(p => p.FirstName == firstName);
                var match = person?.LastName == "UpdatedPerson";
                Logger.Log($"Polling client SecondaryItems: lastName={person?.LastName}");
                return match;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive value update (node still alive)");

        var clientPerson = clientArea.SecondaryItems.First(p => p.FirstName == firstName);
        Assert.Equal("UpdatedPerson", clientPerson.LastName);
        Logger.Log("Verified: Value update received - confirms node still alive and subscribed");

        // Cleanup: remove test items from SecondaryItems
        serverArea.SecondaryItems = serverArea.SecondaryItems.Where(p => p.FirstName != firstName).ToArray();
        Logger.Log("Cleanup: Removed test items from server collections");
    }
}
