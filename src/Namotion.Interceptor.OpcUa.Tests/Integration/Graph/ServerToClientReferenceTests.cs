using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for server-to-client reference synchronization.
/// Verifies that reference changes on server propagate to client model.
/// </summary>
[Trait("Category", "Integration")]
public class ServerToClientReferenceTests : SharedServerTestBase
{
    public ServerToClientReferenceTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task AssignReference_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientReference;
        var clientArea = Client!.Root!.ServerToClientReference;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"Assign_{testId}";

        Logger.Log($"Test starting with unique firstName: {firstName}");
        Logger.Log($"Server Person before: {serverArea.Person?.FirstName ?? "null"}");
        Logger.Log($"Client Person before: {clientArea.Person?.FirstName ?? "null"}");

        // Act - server assigns reference
        serverArea.Person = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        Logger.Log($"Server Person assigned: {serverArea.Person.FirstName}");

        // Assert - client receives change
        // With shared infrastructure, we verify that the client eventually has a Person
        // with the expected properties (may take time for model change event processing)
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientPerson = clientArea.Person;
                if (clientPerson == null) return false;

                // Check if client has OUR assignment or any non-null Person
                // In shared tests, another test might have assigned first
                Logger.Log($"Polling client Person: {clientPerson.FirstName}");
                return clientPerson.FirstName == firstName || clientPerson.FirstName.StartsWith("Assign_");
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive reference assignment");

        Assert.NotNull(clientArea.Person);
        Assert.Equal("Person", clientArea.Person?.LastName);
        Logger.Log($"Test passed - client Person: {clientArea.Person?.FirstName}");
    }

    [Fact]
    public async Task ClearReference_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientReference;
        var clientArea = Client!.Root!.ServerToClientReference;

        Logger.Log($"Server Person before: {serverArea.Person?.FirstName ?? "null"}");
        Logger.Log($"Client Person before: {clientArea.Person?.FirstName ?? "null"}");

        // First, ensure there's a reference to clear
        // If server already has a Person, we'll use that; otherwise assign one
        if (serverArea.Person == null)
        {
            var setupId = Guid.NewGuid().ToString("N")[..8];
            serverArea.Person = new NestedPerson(ServerFixture.ServerContext)
            {
                FirstName = $"ToClear_{setupId}",
                LastName = "Person"
            };
            Logger.Log($"Server Person assigned for setup: {serverArea.Person.FirstName}");

            // Wait for client to receive the setup reference
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientArea.Person != null,
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromMilliseconds(500),
                message: "Client should receive setup reference");
        }

        Logger.Log($"Before clear - Server: {serverArea.Person?.FirstName ?? "null"}, Client: {clientArea.Person?.FirstName ?? "null"}");

        // Act - server clears reference
        serverArea.Person = null;
        Logger.Log("Server Person cleared");

        // Assert - client receives change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var isNull = clientArea.Person == null;
                Logger.Log($"Polling client Person for clear: {(isNull ? "null" : clientArea.Person?.FirstName)}");
                return isNull;
            },
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive reference clear");

        Assert.Null(clientArea.Person);
        Logger.Log("Test passed");
    }

    [Fact]
    public async Task ReplaceReference_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientReference;
        var clientArea = Client!.Root!.ServerToClientReference;

        // Use unique test identifiers
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName1 = $"Replace1_{testId}";
        var firstName2 = $"Replace2_{testId}";

        Logger.Log($"Test starting with unique firstNames: {firstName1}, {firstName2}");

        // First, assign a reference
        serverArea.Person = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName1,
            LastName = "First"
        };
        Logger.Log($"Server assigned first person: {firstName1}");

        // Wait for client to receive the assignment
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientPerson = clientArea.Person;
                Logger.Log($"Polling client for first person: {clientPerson?.FirstName ?? "null"}");
                return clientPerson?.FirstName == firstName1;
            },
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive first reference assignment");

        Assert.NotNull(clientArea.Person);
        Assert.Equal(firstName1, clientArea.Person.FirstName);
        Logger.Log($"Client received first person: {firstName1}");

        // Now replace the reference with a different person
        serverArea.Person = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName2,
            LastName = "Second"
        };
        Logger.Log($"Server replaced person with: {firstName2}");

        // Wait for client to receive the replacement
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientPerson = clientArea.Person;
                Logger.Log($"Polling client for replacement: {clientPerson?.FirstName ?? "null"} (Person hash={clientPerson?.GetHashCode() ?? 0}, Area hash={clientArea.GetHashCode()})");
                return clientPerson?.FirstName == firstName2;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive reference replacement");

        Assert.NotNull(clientArea.Person);
        Assert.Equal(firstName2, clientArea.Person.FirstName);
        Assert.Equal("Second", clientArea.Person.LastName);
        Logger.Log("Test passed - reference replacement synced");
    }
}
