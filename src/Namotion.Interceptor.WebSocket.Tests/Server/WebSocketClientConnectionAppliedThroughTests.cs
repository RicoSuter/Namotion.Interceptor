using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

/// <summary>
/// Unit-level pin of the applied-through counter's stall rule on <see cref="WebSocketClientConnection"/>:
/// a failed apply must stop the count from advancing for the life of the connection, even once a later
/// update applies successfully. Exercises the connection directly, without a transport fault.
/// </summary>
public class WebSocketClientConnectionAppliedThroughTests
{
    [Fact]
    public void WhenAnApplyFails_ThenTheAppliedThroughCountStopsAdvancing()
    {
        // Arrange: a connection exercised directly through its update-received/applied/failed methods,
        // with no live socket and no apply behind them.
        var connection = new WebSocketClientConnection(new CapturingWebSocket(), NullLogger.Instance);

        // Act: one update applies successfully, then one fails, then a later one applies successfully.
        var first = connection.OnUpdateReceived();
        connection.OnUpdateApplied(first);

        connection.OnUpdateReceived();
        connection.OnApplyFailed();

        var third = connection.OnUpdateReceived();
        connection.OnUpdateApplied(third);

        // Assert: the count stays at the last ordinal that applied before the failure. With the first
        // ordinal being 1, asserting only that the value is below it would also pass an implementation
        // that reset the count to zero on failure, which is a stall in name only: it would stop the
        // client from ever retiring anything further on this connection rather than holding it at the
        // point the failure actually reached. Pinning the exact ordinal rules that out, and also rules
        // out the count resuming past the failure to the later success.
        Assert.Equal(first, connection.AppliedThrough);
    }

    [Fact]
    public void WhenEveryApplySucceeds_ThenTheAppliedThroughCountAdvancesToTheLatestOrdinal()
    {
        // Arrange
        var connection = new WebSocketClientConnection(new CapturingWebSocket(), NullLogger.Instance);

        // Act
        var first = connection.OnUpdateReceived();
        connection.OnUpdateApplied(first);
        var second = connection.OnUpdateReceived();
        connection.OnUpdateApplied(second);

        // Assert
        Assert.Equal(second, connection.AppliedThrough);
    }

    [Fact]
    public void WhenAnUnresolvableSubjectIsDropped_ThenTheWarningIdentifiesTheConnection()
    {
        // Arrange: the applier logs the origin it was given, and this connection's only contribution
        // to that log line is its ToString override, which renders as ConnectionId. Nothing else here
        // touches the network, so this is the whole of the documented promise that a dropped inbound
        // update is logged with the connection id, exercised directly.
        var connection = new WebSocketClientConnection(new CapturingWebSocket(), NullLogger.Instance);
        var recordingLogger = new RecordingLogger();
        var root = new TestRoot(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var update = new SubjectUpdate
        {
            Root = null,
            Subjects = new()
            {
                ["ghost-subject"] = new()
                {
                    [nameof(TestRoot.Name)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Ghost" }
                }
            }
        };

        // Act
        root.ApplySubjectUpdate(update, null, ChangeOrigin.FromSource(connection), logger: recordingLogger);

        // Assert
        var warning = Assert.Single(recordingLogger.Warnings);
        Assert.Contains(connection.ConnectionId, warning);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
