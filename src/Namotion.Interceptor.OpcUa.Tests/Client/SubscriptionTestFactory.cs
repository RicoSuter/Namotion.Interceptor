using Microsoft.Extensions.Options;
using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.Subscriptions;
using Opc.Ua.Client.Subscriptions.MonitoredItems;
using MonitoredItemOptions = Opc.Ua.Client.Subscriptions.MonitoredItems.MonitoredItemOptions;
using SubscriptionOptions = Opc.Ua.Client.Subscriptions.SubscriptionOptions;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Builds the subscriptions, monitored items and notifications of the SDK's subscription engine
/// without a server. Only the engine can create the real ones, so they are mocked.
/// </summary>
internal static class SubscriptionTestFactory
{
    private static int _lastClientHandle;

    private delegate bool TryAddMonitoredItem(
        string name, IOptionsMonitor<MonitoredItemOptions> options, out IMonitoredItem? monitoredItem);

    /// <param name="created">Whether the server created the item.</param>
    /// <param name="error">The error of the last attempt to apply the item, or <c>null</c> for none.</param>
    internal static IMonitoredItem CreateMonitoredItem(bool created = true, StatusCode? error = null)
    {
        var clientHandle = (uint)Interlocked.Increment(ref _lastClientHandle);
        var serviceResult = error is { } statusCode ? new ServiceResult(statusCode) : ServiceResult.Good;

        var item = new Mock<IMonitoredItem>();
        item.SetupGet(i => i.ClientHandle).Returns(clientHandle);
        item.SetupGet(i => i.Created).Returns(created);
        item.SetupGet(i => i.Error).Returns(serviceResult);
        return item.Object;
    }

    /// <summary>
    /// Creates a subscription that the server created and that holds <paramref name="items"/>.
    /// </summary>
    /// <param name="items">The items the subscription starts with.</param>
    /// <param name="tryRequeue">Called with the client handle of a requeued item, or <c>null</c> to accept every requeue.</param>
    /// <param name="createItem">Creates the item for options added later, or <c>null</c> for an item the server created.</param>
    /// <param name="created">Whether the server created the subscription.</param>
    internal static ISubscription CreateSubscription(
        IEnumerable<IMonitoredItem>? items = null,
        Func<uint, bool>? tryRequeue = null,
        Func<MonitoredItemOptions, IMonitoredItem>? createItem = null,
        bool created = true)
    {
        var currentItems = new List<IMonitoredItem>(items ?? []);

        var collection = new Mock<IMonitoredItemCollection>();
        // A snapshot, as the engine returns one: the caller removes items while enumerating.
        collection.SetupGet(c => c.Items).Returns(() => currentItems.ToArray());
        collection.SetupGet(c => c.Count).Returns(() => (uint)currentItems.Count);
        collection
            .Setup(c => c.TryRemove(It.IsAny<uint>()))
            .Returns((uint clientHandle) => currentItems.RemoveAll(item => item.ClientHandle == clientHandle) > 0);
        collection
            .Setup(c => c.TryAdd(
                It.IsAny<string>(), It.IsAny<IOptionsMonitor<MonitoredItemOptions>>(), out It.Ref<IMonitoredItem?>.IsAny))
            .Returns(new TryAddMonitoredItem((string _, IOptionsMonitor<MonitoredItemOptions> options, out IMonitoredItem? monitoredItem) =>
            {
                monitoredItem = createItem?.Invoke(options.CurrentValue) ?? CreateMonitoredItem();
                currentItems.Add(monitoredItem);
                return true;
            }));
        collection
            .As<IMonitoredItemRetryCollection>()
            .Setup(c => c.TryRequeue(It.IsAny<uint>()))
            .Returns((uint clientHandle) => tryRequeue?.Invoke(clientHandle) ?? true);

        var subscription = new Mock<ISubscription>();
        subscription.SetupGet(s => s.Created).Returns(created);
        subscription.SetupGet(s => s.MonitoredItems).Returns(collection.Object);
        return subscription.Object;
    }

    /// <summary>
    /// Creates a session whose subscription engine hands out subscriptions from <see cref="CreateSubscription"/>.
    /// </summary>
    /// <param name="createItem">Creates the item for added options, or <c>null</c> for an item the server created.</param>
    /// <param name="subscriptionsCreated">Whether the server creates the subscriptions.</param>
    internal static ISession CreateSession(
        Func<MonitoredItemOptions, IMonitoredItem>? createItem = null,
        bool subscriptionsCreated = true)
    {
        var subscriptionManager = new Mock<ISubscriptionManager>();
        subscriptionManager
            .Setup(m => m.Add(It.IsAny<ISubscriptionNotificationHandler>(), It.IsAny<IOptionsMonitor<SubscriptionOptions>>()))
            .Returns(() => CreateSubscription(createItem: createItem, created: subscriptionsCreated));

        var engine = subscriptionManager.Object;
        var session = new Mock<ISession>();
        session.Setup(s => s.TryGetSubscriptionManager(out engine)).Returns(true);
        return session.Object;
    }

    internal static MonitoredItemRequest CreateRequest(OpcUaSubjectClientSource source, string propertyName)
    {
        var property = new RegisteredSubject((TestRoot)source.RootSubject).TryGetProperty(propertyName)!;
        return new MonitoredItemRequest(property, new MonitoredItemOptions
        {
            StartNodeId = new NodeId(propertyName, 2),
            AttributeId = Opc.Ua.Attributes.Value
        });
    }

    internal static DataValueChange CreateDataChange(
        uint clientHandle, string value, DateTime sourceTimestamp, uint partitionServerId = 0) =>
        new(MonitoredItem: null, new DataValue(value, StatusCodes.Good, sourceTimestamp), DiagnosticInfo: null)
        {
            ClientHandle = clientHandle,
            PartitionServerId = partitionServerId
        };
}
