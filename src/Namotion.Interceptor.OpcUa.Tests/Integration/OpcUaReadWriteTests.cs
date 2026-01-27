using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for basic read/write synchronization using the shared OPC UA server.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaReadWriteTests : SharedServerTestBase
{
    public OpcUaReadWriteTests(SharedOpcUaServerFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task WriteAndReadPrimitives_ShouldUpdateClient()
    {
        // Arrange - use dedicated test area
        var serverArea = Fixture.ServerRoot.ReadWrite.BasicSync;
        var clientArea = Client!.Root!.ReadWrite.BasicSync;

        // Act & Assert - Test string property from server
        serverArea.Name = "Updated Server Name";
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Name == "Updated Server Name",
            timeout: TimeSpan.FromSeconds(60),
            message: "Client should receive server's name update");

        // Test string property from client
        clientArea.Name = "Updated Client Name";
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Updated Client Name",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive client's name update");

        // Test numeric property from server
        serverArea.Number = 123.45m;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Number == 123.45m,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should receive server's number update");

        // Test numeric property from client
        clientArea.Number = 54.321m;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Number == 54.321m,
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive client's number update");
    }

    [Fact]
    public async Task WriteAndReadArraysOnServer_ShouldUpdateClient()
    {
        // Arrange - use dedicated test area
        var serverArea = Fixture.ServerRoot.ReadWrite.ArraySync;
        var clientArea = Client!.Root!.ReadWrite.ArraySync;

        Logger.Log($"Server initial ScalarNumbers: [{string.Join(", ", serverArea.ScalarNumbers)}]");
        Logger.Log($"Client initial ScalarNumbers: [{string.Join(", ", clientArea.ScalarNumbers)}]");

        // Act - update server arrays
        var newNumbers = new[] { 100, 200, 300 };
        serverArea.ScalarNumbers = newNumbers;
        Logger.Log($"Server updated ScalarNumbers: [{string.Join(", ", serverArea.ScalarNumbers)}]");

        // Assert - wait for array synchronization
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.ScalarNumbers.SequenceEqual(newNumbers),
            timeout: TimeSpan.FromSeconds(60),
            message: "Client should receive server's array update");

        Logger.Log($"Client ScalarNumbers after update: [{string.Join(", ", clientArea.ScalarNumbers)}]");
        Assert.Equal(newNumbers, clientArea.ScalarNumbers);
    }
}

/// <summary>
/// Tests for nested structure synchronization using the shared OPC UA server.
/// Tests object references, arrays, dictionaries, deep nesting, OpcUaValue pattern, and PropertyAttribute.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaNestedStructureTests : SharedServerTestBase
{
    public OpcUaNestedStructureTests(SharedOpcUaServerFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task WriteAndReadNestedStructures_ShouldUpdateClient()
    {
        // Arrange - use dedicated Nested test area
        var serverArea = Fixture.ServerRoot.Nested;
        var clientArea = Client!.Root!.Nested;

        // Act: Write all properties at once (no waiting between writes)
        serverArea.Person.FirstName = "UpdatedFirst";              // Test 1: Variable on object reference
        serverArea.People[0].LastName = "UpdatedLast";             // Test 2: Variable on collection item
        serverArea.PeopleByName!["john"].FirstName = "Johnny";     // Test 3: Variable on dictionary item
        serverArea.Person.Address!.City = "New York";              // Test 4: Deep nesting
        serverArea.People[0].Address!.ZipCode = "12345";           // Test 5: Collection + nesting
        serverArea.Sensor!.Value = 42.5;                           // Test 6: OpcUaValue pattern
        serverArea.Sensor.Unit = "°F";                             // Test 7: OpcUaValue child property
        serverArea.Sensor.MinValue = -50.0;                        // Test 8: OpcUaValue child property
        serverArea.Number_Unit = "items";                          // Test 9: PropertyAttribute subnode

        // Assert: Wait for all properties to sync in single check
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
            timeout: TimeSpan.FromSeconds(60),
            message: "Client should receive all nested structure updates");

        Logger.Log("All nested structure tests passed!");
    }
}
