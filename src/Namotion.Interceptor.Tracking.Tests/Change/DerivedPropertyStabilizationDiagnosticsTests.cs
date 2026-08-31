using System.Collections.Concurrent;
using System.Diagnostics;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(DerivedPropertyTraceCollection.Name)]
public class DerivedPropertyStabilizationDiagnosticsTests
{
    private sealed class RecordingTraceListener : TraceListener
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        public override void Write(string? message) => Messages.Enqueue(message ?? string.Empty);

        public override void WriteLine(string? message) => Messages.Enqueue(message ?? string.Empty);
    }

    [Fact]
    public void WhenDependencyStabilizationSucceeds_ThenNoExhaustionWarningIsEmitted()
    {
        // Arrange
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);

        try
        {
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            // Act
            _ = new SideEffectWritePerson(context);

            // Assert
            Assert.DoesNotContain(listener.Messages, message =>
                message.Contains("MaxStabilizationIterations") &&
                message.Contains(nameof(SideEffectWritePerson)));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DerivedPropertyTraceCollection
{
    public const string Name = "Derived property trace";
}
