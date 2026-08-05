using System.Reflection;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Throwaway review test: reproduces the previously demonstrated failure where a detach on
/// another thread flushed the flush loop's mid-write node state back into the subject.
/// </summary>
[Trait("Category", "Integration")]
public class SelfEchoReproTests
{
    private readonly ITestOutputHelper _output;

    public SelfEchoReproTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenDetachRacesTheFlushLoopMidWrite_ThenTheStaleValueIsNotAppliedBack()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<SelfWriteTestParent>(logger);
        await server.StartAsync(
            createRoot: context => new SelfWriteTestParent(context),
            initializeDefaults: (context, root) =>
                root.Child = new SelfWriteTestChild(context) { Value = "initial" },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var serverService = (OpcUaSubjectServer)server.Server!;
        var child = server.Root!.Child!;
        var property = new PropertyReference(child, nameof(SelfWriteTestChild.Value));

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverService.TryGetVariableNode(property, out _),
            message: "the child's variable node should exist");

        Assert.True(serverService.TryGetVariableNode(property, out var node));

        var standardServer = (OpcUaStandardServer)serverService.CurrentServer!;
        var nodeManagerLock = standardServer.NodeManagerLock!;
        var systemContext = standardServer.CurrentInstance.DefaultSystemContext;

        var flagField = typeof(OpcUaSubjectServer).GetField(
            "_isWritingOwnNodeValues", BindingFlags.NonPublic | BindingFlags.Static)!;

        using var valueAssigned = new ManualResetEventSlim();
        using var detachRequested = new ManualResetEventSlim();
        Exception? flushError = null;
        Exception? detachError = null;
        Thread? detachThread = null;

        // Simulates the flush loop's exact state between assigning a node value and its own
        // ClearChangeMasks: NodeManagerLock held, node.Value set, Value change mask pending.
        var flushThread = new Thread(() =>
        {
            try
            {
                lock (nodeManagerLock)
                {
                    node!.Value = "stale-echo";
                    valueAssigned.Set();
                    Assert.True(detachRequested.Wait(TimeSpan.FromSeconds(10)));

                    // Wait (bounded, no fixed sleep) until the detach thread is either blocked on a
                    // lock (correct code: RemoveSubjectNodes waits for NodeManagerLock; defective
                    // code: the echo has already fired and DeleteNode blocks at RemoveRootNotifier)
                    // or has finished entirely. Both outcomes are terminal for the interleaving, so
                    // proceeding is deterministic in both directions. The only wait on the detach
                    // thread after detachRequested.Set() is a lock, so WaitSleepJoin is unambiguous.
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                    while (detachThread!.IsAlive &&
                           (detachThread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
                    {
                        Assert.True(DateTime.UtcNow < deadline, "detach thread should block or finish");
                        Thread.SpinWait(1000);
                    }

                    flagField.SetValue(null, true);
                    try
                    {
                        node.ClearChangeMasks(systemContext, false);
                    }
                    finally
                    {
                        flagField.SetValue(null, false);
                    }
                }
            }
            catch (Exception exception)
            {
                flushError = exception;
            }
        });

        detachThread = new Thread(() =>
        {
            try
            {
                Assert.True(valueAssigned.Wait(TimeSpan.FromSeconds(10)));
                detachRequested.Set();
                server.Root.Child = null;
            }
            catch (Exception exception)
            {
                detachError = exception;
            }
        });

        // Act
        flushThread.Start();
        detachThread.Start();

        Assert.True(flushThread.Join(TimeSpan.FromSeconds(30)), "flush thread should finish (no deadlock)");
        Assert.True(detachThread.Join(TimeSpan.FromSeconds(30)), "detach thread should finish (no deadlock)");
        Assert.Null(flushError);
        Assert.Null(detachError);

        // Assert: the mid-write value must not have been applied back to the subject.
        Assert.Equal("initial", child.Value);
        Assert.Equal(0d, serverService.Diagnostics.IncomingChangesPerSecond);
    }
}
