using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for array edge cases: empty arrays, resize operations.
/// Tests bidirectional sync without explicit transactions.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaCollectionEdgeCaseTests : SharedServerTestBase
{
    public OpcUaCollectionEdgeCaseTests(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
        : base(serverFixture, clientFixture, output) { }

    [Fact]
    public async Task EmptyArray_ServerToClient_ShouldSync()
    {
        var serverArea = ServerFixture.ServerRoot.Collections;
        var clientArea = Client!.Root!.Collections;

        // Server to client: set to empty
        serverArea.IntArray = [];
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.IntArray.Length == 0,
            timeout: TimeSpan.FromSeconds(60),
            message: "Client should receive empty array from server");

        // Server to client: set back to non-empty
        serverArea.IntArray = [42, 43];
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.IntArray.Length == 2 && clientArea.IntArray.SequenceEqual([42, 43]),
            timeout: TimeSpan.FromSeconds(60),
            message: "Client should receive non-empty array from server");

        Logger.Log("Empty array server→client sync verified");
    }

    [Fact]
    public async Task ArrayResize_GrowAndShrink_ServerToClient_ShouldSync()
    {
        var serverArea = ServerFixture.ServerRoot.Collections;
        var clientArea = Client!.Root!.Collections;

        // Start with 3 items
        serverArea.IntArray = [1, 2, 3];
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.IntArray.Length == 3,
            timeout: TimeSpan.FromSeconds(60),
            message: "Client should have 3 items");

        // Grow to 5 items
        serverArea.IntArray = [1, 2, 3, 4, 5];
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.IntArray.Length == 5 && clientArea.IntArray[4] == 5,
            timeout: TimeSpan.FromSeconds(60),
            message: "Client should grow to 5 items");

        // Shrink to 2 items
        serverArea.IntArray = [10, 20];
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.IntArray.Length == 2 &&
                  clientArea.IntArray[0] == 10 &&
                  clientArea.IntArray[1] == 20,
            timeout: TimeSpan.FromSeconds(60),
            message: "Client should shrink to 2 items with new values");

        Logger.Log("Array resize server→client sync verified");
    }
}
