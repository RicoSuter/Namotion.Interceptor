using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for client-to-server reference synchronization.
/// Verifies that reference changes on client propagate to server model.
/// CLIENT makes changes, SERVER receives them.
/// </summary>
[Trait("Category", "Integration")]
public class ClientToServerReferenceTests : SharedServerTestBase
{
    public ClientToServerReferenceTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task AssignReference_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerReference;
        var serverArea = ServerFixture.ServerRoot.ClientToServerReference;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"Assign_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");
        Logger.Log($"Client Person before: {clientArea.Person?.FirstName ?? "null"}");
        Logger.Log($"Server Person before: {serverArea.Person?.FirstName ?? "null"}");

        // Act - client assigns reference (using client context for creating new instances)
        clientArea.Person = new NestedPerson(Client!.Context)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        Logger.Log($"Client Person assigned: {clientArea.Person.FirstName}");

        // Assert - server receives change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.Person;
                if (serverPerson == null) return false;

                Logger.Log($"Polling server Person: {serverPerson.FirstName}");
                return serverPerson.FirstName == firstName || serverPerson.FirstName.StartsWith("Assign_");
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive reference assignment from client");

        Assert.NotNull(serverArea.Person);
        Assert.Equal("Person", serverArea.Person?.LastName);
        Logger.Log($"Test passed - server Person: {serverArea.Person?.FirstName}");
    }

    [Fact]
    public async Task ClearReference_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerReference;
        var serverArea = ServerFixture.ServerRoot.ClientToServerReference;

        Logger.Log($"Client Person before: {clientArea.Person?.FirstName ?? "null"}");
        Logger.Log($"Server Person before: {serverArea.Person?.FirstName ?? "null"}");

        // First, ensure there's a reference to clear
        // If client already has a Person, we'll use that; otherwise assign one
        if (clientArea.Person == null)
        {
            var setupId = Guid.NewGuid().ToString("N")[..8];
            clientArea.Person = new NestedPerson(Client!.Context)
            {
                FirstName = $"ToClear_{setupId}",
                LastName = "Person"
            };
            Logger.Log($"Client Person assigned for setup: {clientArea.Person.FirstName}");

            // Wait for server to receive the setup reference
            await AsyncTestHelpers.WaitUntilAsync(
                () => serverArea.Person != null,
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromMilliseconds(500),
                message: "Server should receive setup reference from client");
        }

        Logger.Log($"Before clear - Client: {clientArea.Person?.FirstName ?? "null"}, Server: {serverArea.Person?.FirstName ?? "null"}");

        // Act - client clears reference
        clientArea.Person = null;
        Logger.Log("Client Person cleared");

        // Assert - server receives change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var isNull = serverArea.Person == null;
                Logger.Log($"Polling server Person for clear: {(isNull ? "null" : serverArea.Person?.FirstName)}");
                return isNull;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive reference clear from client");

        Assert.Null(serverArea.Person);
        Logger.Log("Test passed");
    }
}
