using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for client-to-server shared subject synchronization.
/// Verifies that adding/removing items that are already referenced in another collection
/// correctly propagates from client to server.
/// </summary>
[Trait("Category", "Integration")]
public class ClientToServerSharedSubjectTests : SharedServerTestBase
{
    public ClientToServerSharedSubjectTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task AddToSecondCollection_ServerReceivesReferenceAdded()
    {
        var clientArea = Client!.Root!.ClientToServerSharedSubject;
        var serverArea = ServerFixture.ServerRoot.ClientToServerSharedSubject;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"Shared_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");
        Logger.Log($"Client PrimaryItems before: {clientArea.PrimaryItems.Length}");
        Logger.Log($"Client SecondaryItems before: {clientArea.SecondaryItems.Length}");
        Logger.Log($"Server PrimaryItems before: {serverArea.PrimaryItems.Length}");
        Logger.Log($"Server SecondaryItems before: {serverArea.SecondaryItems.Length}");

        // Act - Client adds a person to PrimaryItems
        var sharedPerson = new NestedPerson(Client.Context)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        clientArea.PrimaryItems = [..clientArea.PrimaryItems, sharedPerson];
        Logger.Log($"Client added person to PrimaryItems: {sharedPerson.FirstName}");

        // Wait for server to receive the first addition
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPrimary = serverArea.PrimaryItems;
                var hasPerson = serverPrimary.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling server PrimaryItems: count={serverPrimary.Length}, hasPerson={hasPerson}");
                return hasPerson;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's PrimaryItems add");

        Logger.Log($"Server received person in PrimaryItems");

        // Now add the same person to SecondaryItems (shared subject)
        clientArea.SecondaryItems = [..clientArea.SecondaryItems, sharedPerson];
        Logger.Log($"Client added same person to SecondaryItems");

        // Assert - Server receives the shared reference in SecondaryItems
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverSecondary = serverArea.SecondaryItems;
                var hasPerson = serverSecondary.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling server SecondaryItems: count={serverSecondary.Length}, hasPerson={hasPerson}");
                return hasPerson;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's SecondaryItems add (shared subject)");

        // Verify both collections on server have the person
        Assert.Contains(serverArea.PrimaryItems, p => p.FirstName == firstName);
        Assert.Contains(serverArea.SecondaryItems, p => p.FirstName == firstName);
        Logger.Log("Test passed - server received shared subject in both collections");
    }

    [Fact]
    public async Task RemoveFromOneCollection_ServerReceivesReferenceDeleted()
    {
        var clientArea = Client!.Root!.ClientToServerSharedSubject;
        var serverArea = ServerFixture.ServerRoot.ClientToServerSharedSubject;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"Remove_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");

        // Setup - Client adds a person to both collections
        var sharedPerson = new NestedPerson(Client.Context)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        clientArea.PrimaryItems = [..clientArea.PrimaryItems, sharedPerson];
        clientArea.SecondaryItems = [..clientArea.SecondaryItems, sharedPerson];
        Logger.Log($"Client added person to both collections: {sharedPerson.FirstName}");

        // Wait for server to have the person in both collections
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var inPrimary = serverArea.PrimaryItems.Any(p => p.FirstName == firstName);
                var inSecondary = serverArea.SecondaryItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling server: inPrimary={inPrimary}, inSecondary={inSecondary}");
                return inPrimary && inSecondary;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should have person in both collections");

        Logger.Log("Server has person in both collections");

        // Act - Client removes the person from SecondaryItems only
        clientArea.SecondaryItems = clientArea.SecondaryItems
            .Where(p => p.FirstName != firstName)
            .ToArray();
        Logger.Log("Client removed person from SecondaryItems");

        // Assert - Server removes from SecondaryItems but keeps in PrimaryItems
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var inSecondary = serverArea.SecondaryItems.Any(p => p.FirstName == firstName);
                Logger.Log($"Polling server SecondaryItems for removal: stillPresent={inSecondary}");
                return !inSecondary;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should remove person from SecondaryItems");

        // Verify person is removed from SecondaryItems but still in PrimaryItems
        Assert.DoesNotContain(serverArea.SecondaryItems, p => p.FirstName == firstName);
        Assert.Contains(serverArea.PrimaryItems, p => p.FirstName == firstName);
        Logger.Log("Test passed - server removed from SecondaryItems but kept in PrimaryItems");
    }
}
