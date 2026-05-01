using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests;

public partial class OpcUaCurrentSessionChangedTests
{
    [InterceptorSubject]
    public partial class TestSubject
    {
        public partial string Name { get; set; }
    }

    private static OpcUaSubjectClientSource CreateSource()
    {
        // Arrange a minimal client source that exercises only the event-firing path.
        // No real OPC UA traffic is needed - we drive OnCurrentSessionChanged directly.
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var subject = new TestSubject(context);
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        };
        return new OpcUaSubjectClientSource(subject, configuration, NullLogger.Instance);
    }

    [Fact]
    public void WhenHandlerThrows_ThenInvocationDoesNotPropagate()
    {
        // Arrange
        var source = CreateSource();
        source.CurrentSessionChanged += (_, _) => throw new InvalidOperationException("bang");

        // Act & Assert - exception is caught inside OnCurrentSessionChanged and logged,
        // not re-thrown. A throwing handler must not be able to break the connector.
        var exception = Record.Exception(() => source.OnCurrentSessionChanged(null, null));
        Assert.Null(exception);
    }

    [Fact]
    public void WhenNoHandlerSubscribed_ThenInvocationIsNoOp()
    {
        // Arrange
        var source = CreateSource();

        // Act & Assert - the null-handler fast path must not throw.
        var exception = Record.Exception(() => source.OnCurrentSessionChanged(null, null));
        Assert.Null(exception);
    }

    [Fact]
    public void WhenInvoked_ThenEventArgsCarryPreviousAndCurrent()
    {
        // Arrange
        var source = CreateSource();
        OpcUaCurrentSessionChangedEventArgs? captured = null;
        source.CurrentSessionChanged += (_, args) => captured = args;

        // Act - drive the event with both sides null; the contract is that the handler
        // sees exactly the values the connector passed.
        source.OnCurrentSessionChanged(null, null);

        // Assert
        Assert.NotNull(captured);
        Assert.Null(captured.PreviousSession);
        Assert.Null(captured.CurrentSession);
    }
}
