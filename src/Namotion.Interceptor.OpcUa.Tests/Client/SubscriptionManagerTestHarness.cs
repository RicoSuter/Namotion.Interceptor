using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Shared test harness for SubscriptionManager unit tests.
/// Wires up a DynamicSubject, OpcUaSubjectClientSource, and SubscriptionManager
/// without a live OPC UA session. The SubjectPropertyWriter is put into the
/// applying (non-buffering) state by calling StartBuffering + LoadInitialStateAndResumeAsync
/// against a mock ISubjectSource that returns null initial state.
/// </summary>
internal sealed class SubscriptionManagerTestHarness
{
    private readonly IInterceptorSubject _subject;

    public SubscriptionManager Manager { get; }
    public OpcUaSubjectClientSource Source { get; }
    public SubjectPropertyWriter PropertyWriter { get; }

    private SubscriptionManagerTestHarness(
        IInterceptorSubject subject,
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        SubscriptionManager manager)
    {
        _subject = subject;
        Source = source;
        PropertyWriter = propertyWriter;
        Manager = manager;
    }

    public static SubscriptionManagerTestHarness Create()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var subject = new DynamicSubject(context);

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(false)
        };

        var source = new OpcUaSubjectClientSource(subject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        // The SubjectPropertyWriter buffers updates until LoadInitialStateAndResumeAsync is called
        // (its _updates field starts as a non-null list at construction). We need it in the
        // applying state (i.e., _updates == null) so that ApplyDataChange actually updates subjects
        // in tests. We back the writer with a mock ISubjectSource that returns null initial state
        // so that LoadInitialStateAndResumeAsync succeeds without a live session.
        var mockSource = new Mock<ISubjectSource>();
        mockSource
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var propertyWriter = new SubjectPropertyWriter(mockSource.Object, NullLogger.Instance);
        propertyWriter.StartBuffering();
        propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None).GetAwaiter().GetResult();

        var manager = new SubscriptionManager(
            source,
            propertyWriter,
            pollingManager: null,
            readAfterWriteManager: null,
            configuration,
            NullLogger<OpcUaSubjectClientSource>.Instance);

        return new SubscriptionManagerTestHarness(subject, source, propertyWriter, manager);
    }

    /// <summary>
    /// Adds a dynamic double property to the subject and registers it in the manager's
    /// monitored items dictionary under the given client handle.
    /// </summary>
    public RegisteredSubjectProperty RegisterMonitoredItem(uint clientHandle, string propertyName)
    {
        var registeredSubject = _subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Subject has no registered subject. Ensure context has WithRegistry().");

        double storedValue = 0d;
        var property = registeredSubject.AddProperty<double>(
            propertyName,
            getValue: _ => storedValue,
            setValue: (_, value) => storedValue = value is double d ? d : 0d);

        Manager.MonitoredItemsForTesting[clientHandle] = property;

        return property;
    }

    /// <summary>
    /// Reads the current value of a dynamic property by name.
    /// </summary>
    public object? GetValue(string name)
    {
        var registeredSubject = _subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Subject has no registered subject.");

        return registeredSubject.TryGetProperty(name)?.GetValue();
    }
}
