using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// The OPC UA client's work per data change notification and per outgoing write, without a session: the
/// notification never crosses a socket, and the write values are built but never sent.
/// </summary>
[MemoryDiagnoser]
public class OpcUaClientBenchmark
{
    private const int ScalarKindCount = 5;
    private const int ArrayLength = 10;

    private OpcUaSubjectClientSource _source;
    private SubscriptionManager _subscriptionManager;
    private Subscription _subscription;
    private OpcUaValueConverter _valueConverter;
    private Exception? _reportedError;

    private DataChangeNotification[] _notifications;
    private byte[][] _encodedNotifications;
    private object[][] _notificationValues;
    private ServiceMessageContext _messageContext;
    private IList<string> _stringTable;
    private int _nextNotification;
    private uint _sequenceNumber;

    private RegisteredSubjectProperty[] _properties;
    private NodeId[] _nodeIds;
    private object[] _propertyValues;
    private DateTimeOffset _changedTimestamp;

    [Params(1, 100)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        var nodes = new OpcUaBenchmarkNode[(ItemCount + ScalarKindCount - 1) / ScalarKindCount];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new OpcUaBenchmarkNode();
        }

        var root = new OpcUaBenchmarkRoot(context) { Nodes = nodes };

        _valueConverter = new OpcUaValueConverter();
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = _valueConverter,
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        };

        _source = new OpcUaSubjectClientSource(root, configuration, NullLogger.Instance);

        // Without this the writer buffers every notification instead of applying it.
        var propertyWriter = new SubjectPropertyWriter(_source, NullLogger.Instance);
        await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);

        _subscriptionManager = new SubscriptionManager(
            _source, propertyWriter, pollingManager: null, readAfterWriteManager: null,
            configuration, error => _reportedError ??= error, NullLogger.Instance);

        _subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());

        _properties = new RegisteredSubjectProperty[ItemCount];
        _nodeIds = new NodeId[ItemCount];
        _propertyValues = new object[ItemCount];
        _changedTimestamp = DateTimeOffset.UtcNow;

        var sourceTimestamp = DateTime.UtcNow;
        var firstItems = new MonitoredItemNotification[ItemCount];
        var secondItems = new MonitoredItemNotification[ItemCount];
        _notificationValues = [new object[ItemCount], new object[ItemCount]];
        for (var i = 0; i < ItemCount; i++)
        {
            var (propertyName, firstValue, secondValue) = GetItem(i);
            var property = nodes[i / ScalarKindCount].TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
            var nodeId = new NodeId($"Node{i / ScalarKindCount}.{propertyName}", 2);

            var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
            {
                StartNodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                Handle = property
            };
            _subscription.AddItem(monitoredItem);
            _subscriptionManager.TrackMonitoredItem(monitoredItem);

            firstItems[i] = CreateItemNotification(monitoredItem.ClientHandle, firstValue, sourceTimestamp);
            secondItems[i] = CreateItemNotification(monitoredItem.ClientHandle, secondValue, sourceTimestamp);

            _notificationValues[0][i] = firstValue;
            _notificationValues[1][i] = secondValue;

            _properties[i] = property;
            _nodeIds[i] = nodeId;
            _propertyValues[i] = firstValue;
        }

        _notifications = [CreateNotification(firstItems), CreateNotification(secondItems)];
        _messageContext = new ServiceMessageContext(NullTelemetryContext.Instance);
        _encodedNotifications = [Encode(_notifications[0]), Encode(_notifications[1])];
        _stringTable = Array.Empty<string>();

        // A failed apply is only logged and reported, so without these checks the benchmark could measure
        // a path that stores nothing.
        ReceiveDataChange();
        ThrowIfNotApplied(_notificationValues[0]);
        DecodeAndReceiveDataChange();
        ThrowIfNotApplied(_notificationValues[1]);
    }

    /// <summary>
    /// The client's data change handler for one prebuilt notification message of <see cref="ItemCount"/>
    /// items: value unpacking, conversion and applying the changes through the property writer.
    /// </summary>
    /// <remarks>
    /// A prebuilt message leaves out whatever the SDK allocates per value while decoding, which
    /// <see cref="DecodeAndReceiveDataChange"/> includes.
    /// </remarks>
    [Benchmark]
    public void ReceiveDataChange()
    {
        // Two messages with different values take turns, because the equality check drops a value that
        // equals the current one before it reaches the rest of the write chain.
        var notification = _notifications[_nextNotification];
        _nextNotification ^= 1;
        notification.SequenceNumber = ++_sequenceNumber;

        _subscriptionManager.OnFastDataChange(_subscription, notification, _stringTable);
    }

    /// <summary>
    /// <see cref="ReceiveDataChange"/> preceded by decoding the notification message from its binary
    /// encoding, the way a message arrives from the server.
    /// </summary>
    [Benchmark]
    public void DecodeAndReceiveDataChange()
    {
        var encodedNotification = _encodedNotifications[_nextNotification];
        _nextNotification ^= 1;

        using var decoder = new BinaryDecoder(encodedNotification, _messageContext);
        var notification = (DataChangeNotification)decoder.ReadEncodeable(null, typeof(DataChangeNotification));
        notification.SequenceNumber = ++_sequenceNumber;

        _subscriptionManager.OnFastDataChange(_subscription, notification, _stringTable);
    }

    /// <summary>
    /// Builds the write request for <see cref="ItemCount"/> changed properties the way
    /// <c>OutboundWriter.CreateWriteValuesCollection</c> does, up to what is handed to the session.
    /// </summary>
    [Benchmark]
    public WriteValueCollection CreateWriteValues()
    {
        var writeValues = new WriteValueCollection(_properties.Length);
        for (var i = 0; i < _properties.Length; i++)
        {
            var convertedValue = _valueConverter.ConvertToNodeValue(_propertyValues[i], _properties[i]);

            writeValues.Add(new WriteValue
            {
                NodeId = _nodeIds[i],
                AttributeId = Opc.Ua.Attributes.Value,
                Value = new DataValue
                {
                    Value = convertedValue,
                    StatusCode = StatusCodes.Good,
                    SourceTimestamp = _changedTimestamp.UtcDateTime
                }
            });
        }

        return writeValues;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _subscriptionManager.DisposeAsync();
        _subscription.Dispose();
        await _source.DisposeAsync();

        ThrowIfErrorWasReported();
    }

    private void ThrowIfNotApplied(object[] expectedValues)
    {
        ThrowIfErrorWasReported();

        for (var i = 0; i < expectedValues.Length; i++)
        {
            var expectedValue = expectedValues[i];
            var actualValue = _properties[i].GetValue();
            var isApplied = expectedValue is double[] expectedArray
                ? actualValue is double[] actualArray && expectedArray.AsSpan().SequenceEqual(actualArray)
                : Equals(actualValue, expectedValue);

            if (!isApplied)
            {
                throw new InvalidOperationException(
                    $"The notification was not applied to {_properties[i].Name}: expected {expectedValue}, found {actualValue}.");
            }
        }
    }

    private void ThrowIfErrorWasReported()
    {
        if (_reportedError is not null)
        {
            throw new InvalidOperationException("The data change handler reported an error.", _reportedError);
        }
    }

    /// <summary>
    /// Cycles through the scalar kinds, and the last item of a message with more than one item is the array.
    /// </summary>
    private (string PropertyName, object FirstValue, object SecondValue) GetItem(int index)
    {
        if (ItemCount > 1 && index == ItemCount - 1)
        {
            var firstArray = new double[ArrayLength];
            var secondArray = new double[ArrayLength];
            for (var i = 0; i < ArrayLength; i++)
            {
                firstArray[i] = index + i + 0.25;
                secondArray[i] = index + i + 0.75;
            }

            return (nameof(OpcUaBenchmarkNode.DoubleArrayValue), firstArray, secondArray);
        }

        return (index % ScalarKindCount) switch
        {
            0 => (nameof(OpcUaBenchmarkNode.DoubleValue), index + 0.25, index + 0.75),
            1 => (nameof(OpcUaBenchmarkNode.Int32Value), index, index + 1),
            2 => (nameof(OpcUaBenchmarkNode.BooleanValue), false, true),
            3 => (nameof(OpcUaBenchmarkNode.StringValue), $"First value {index}", $"Second value {index}"),
            _ => (nameof(OpcUaBenchmarkNode.DateTimeValue),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index),
                new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index))
        };
    }

    private static MonitoredItemNotification CreateItemNotification(uint clientHandle, object value, DateTime sourceTimestamp)
    {
        return new MonitoredItemNotification
        {
            ClientHandle = clientHandle,
            Value = new DataValue
            {
                Value = value,
                StatusCode = StatusCodes.Good,
                SourceTimestamp = sourceTimestamp
            }
        };
    }

    private static DataChangeNotification CreateNotification(MonitoredItemNotification[] items)
    {
        return new DataChangeNotification
        {
            MonitoredItems = [.. items],
            DiagnosticInfos = []
        };
    }

    private byte[] Encode(DataChangeNotification notification)
    {
        using var encoder = new BinaryEncoder(_messageContext);
        encoder.WriteEncodeable(null, notification, typeof(DataChangeNotification));
        return encoder.CloseAndReturnBuffer();
    }
}

[InterceptorSubject]
public partial class OpcUaBenchmarkRoot
{
    public OpcUaBenchmarkRoot()
    {
        Nodes = [];
    }

    public partial OpcUaBenchmarkNode[] Nodes { get; set; }
}

[InterceptorSubject]
public partial class OpcUaBenchmarkNode
{
    public OpcUaBenchmarkNode()
    {
        StringValue = string.Empty;
        DoubleArrayValue = [];
    }

    public partial double DoubleValue { get; set; }

    public partial int Int32Value { get; set; }

    public partial bool BooleanValue { get; set; }

    public partial string StringValue { get; set; }

    public partial DateTime DateTimeValue { get; set; }

    public partial double[] DoubleArrayValue { get; set; }
}
