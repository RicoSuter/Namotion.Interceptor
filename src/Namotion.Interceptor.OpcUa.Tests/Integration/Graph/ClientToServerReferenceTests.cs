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

        // Always set up a fresh reference with unique ID for this test
        var setupId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"ToClear_{setupId}";
        clientArea.Person = new NestedPerson(Client!.Context)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        Logger.Log($"Client Person assigned for setup: {firstName}");

        // Wait for server to receive the setup reference with our specific ID
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Person?.FirstName == firstName,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Server should receive setup reference '{firstName}' from client");

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

    [Fact]
    public async Task ReplaceReference_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerReference;
        var serverArea = ServerFixture.ServerRoot.ClientToServerReference;

        // Use unique test identifiers
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName1 = $"Replace1_{testId}";
        var firstName2 = $"Replace2_{testId}";

        Logger.Log($"Test starting with unique firstNames: {firstName1}, {firstName2}");

        // First, assign a reference
        clientArea.Person = new NestedPerson(Client!.Context)
        {
            FirstName = firstName1,
            LastName = "First"
        };
        Logger.Log($"Client assigned first person: {firstName1}");

        // Wait for server to receive the assignment
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.Person;
                Logger.Log($"Polling server for first person: {serverPerson?.FirstName ?? "null"}");
                return serverPerson?.FirstName == firstName1;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive first reference assignment");

        Assert.NotNull(serverArea.Person);
        Assert.Equal(firstName1, serverArea.Person.FirstName);
        Logger.Log($"Server received first person: {firstName1}");

        // Now replace the reference with a different person
        clientArea.Person = new NestedPerson(Client!.Context)
        {
            FirstName = firstName2,
            LastName = "Second"
        };
        Logger.Log($"Client replaced person with: {firstName2}");

        // Wait for server to receive the replacement
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.Person;
                Logger.Log($"Polling server for replacement: {serverPerson?.FirstName ?? "null"}");
                return serverPerson?.FirstName == firstName2;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive reference replacement");

        Assert.NotNull(serverArea.Person);
        Assert.Equal(firstName2, serverArea.Person.FirstName);
        Assert.Equal("Second", serverArea.Person.LastName);
        Logger.Log("Test passed - reference replacement synced");
    }
}
