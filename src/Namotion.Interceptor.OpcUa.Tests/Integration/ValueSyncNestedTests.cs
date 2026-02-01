using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for nested structure synchronization (server→client) using the shared OPC UA server.
/// Tests object references, arrays, dictionaries, deep nesting, OpcUaValue pattern, and PropertyAttribute.
/// </summary>
[Trait("Category", "Integration")]
public class ValueSyncNestedTests : SharedServerTestBase
{
    public ValueSyncNestedTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task WriteAndReadNestedStructures_ShouldUpdateClient()
    {
        var serverArea = ServerFixture.ServerRoot.Nested;
        var clientArea = Client!.Root!.Nested;

        // Write all properties at once (no waiting between writes)
        serverArea.Person.FirstName = "UpdatedFirst";
        serverArea.People[0].LastName = "UpdatedLast";
        serverArea.PeopleByName!["john"].FirstName = "Johnny";
        serverArea.Person.Address!.City = "New York";
        serverArea.People[0].Address!.ZipCode = "12345";
        serverArea.Sensor!.Value = 42.5;
        serverArea.Sensor.Unit = "°F";
        serverArea.Sensor.MinValue = -50.0;
        serverArea.Number_Unit = "items";

        // Wait for all properties to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Person.FirstName == "UpdatedFirst" &&
                  clientArea.People[0].LastName == "UpdatedLast" &&
                  clientArea.PeopleByName!["john"].FirstName == "Johnny" &&
                  clientArea.Person.Address!.City == "New York" &&
                  clientArea.People[0].Address!.ZipCode == "12345" &&
                  Math.Abs(clientArea.Sensor!.Value - 42.5) < 0.01 &&
                  clientArea.Sensor!.Unit == "°F" &&
                  clientArea.Sensor?.MinValue == -50.0 &&
                  clientArea.Number_Unit == "items",
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive all nested structure updates");

        Logger.Log("All nested structure tests passed!");
    }
}