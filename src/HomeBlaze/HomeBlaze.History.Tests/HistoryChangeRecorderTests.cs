using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.Tests;

public class HistoryChangeRecorderTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingEngine : IHistoryRecorder
    {
        public List<(string Path, DateTimeOffset Timestamp, object? Value, Type Type)> Records { get; } = new();

        public List<(DateTimeOffset Timestamp, string From, string To)> Moves { get; } = new();

        public bool TryRecord(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
        {
            Records.Add((propertyPath, timestamp, value, propertyType));
            return true;
        }

        public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath) =>
            Moves.Add((timestamp, fromPath, toPath));
    }

    private static (RecorderTestSubject Subject, IInterceptorSubjectContext Context) NewSubject()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        return (new RecorderTestSubject(context), context);
    }

    private static SubjectPropertyChange ChangeFor(
        RecorderTestSubject subject, string propertyName, double newValue, DateTimeOffset timestamp) =>
        SubjectPropertyChange.Create(
            new PropertyReference(subject, propertyName), ChangeOrigin.Local, timestamp, null, 0d, newValue);

    [Fact]
    public async Task WhenASubjectLeavesTheGraph_ThenEachRecordedPropertyIsClosedOff()
    {
        // Arrange - a property recorded while the subject was present. Without a closing sample, Last
        // and TimeWeightedAverage carry that reading forward for as long as coverage claims the range,
        // so a removed sensor keeps charting its final value indefinitely.
        var (subject, _) = NewSubject();
        var engine = new RecordingEngine();
        var recorder = new HistoryChangeRecorder(
            engine, new PathResolverStub(), () => Base.AddMinutes(5));

        await recorder.RecordBatch(new[] { ChangeFor(subject, nameof(RecorderTestSubject.Temperature), 21.5d, Base) });
        Assert.Single(engine.Records);

        // Act
        recorder.Forget(subject);

        // Assert - an explicit null at the instant the subject left, which both engines already treat
        // as terminating the held value rather than as a reading of zero.
        Assert.Equal(2, engine.Records.Count);
        var tombstone = engine.Records[1];
        Assert.Equal(engine.Records[0].Path, tombstone.Path);
        Assert.Null(tombstone.Value);
        Assert.Equal(Base.AddMinutes(5), tombstone.Timestamp);
        Assert.Equal(typeof(double), tombstone.Type);
    }

    [Fact]
    public void WhenASubjectNeverRecordedAnything_ThenLeavingTheGraphWritesNothing()
    {
        // Arrange - subjects with no history-eligible property must not produce samples on detach.
        var (subject, _) = NewSubject();
        var engine = new RecordingEngine();
        var recorder = new HistoryChangeRecorder(
            engine, new PathResolverStub(), () => Base.AddMinutes(5));

        // Act
        recorder.Forget(subject);

        // Assert
        Assert.Empty(engine.Records);
    }

    [Fact]
    public async Task WhenASubjectIsForgottenTwice_ThenItIsClosedOffOnlyOnce()
    {
        // Arrange - detach can be dispatched more than once for the same subject, and a second closing
        // sample would show as a spurious gap after the property had already ended.
        var (subject, _) = NewSubject();
        var engine = new RecordingEngine();
        var recorder = new HistoryChangeRecorder(
            engine, new PathResolverStub(), () => Base.AddMinutes(5));

        await recorder.RecordBatch(new[] { ChangeFor(subject, nameof(RecorderTestSubject.Temperature), 21.5d, Base) });

        // Act
        recorder.Forget(subject);
        recorder.Forget(subject);

        // Assert
        Assert.Equal(2, engine.Records.Count);
    }

    private sealed class PathResolverStub : ISubjectPathResolver
    {
        public string? GetPath(IInterceptorSubject subject, PathStyle style) => "/";

        public IReadOnlyList<string> GetPaths(IInterceptorSubject subject, PathStyle style) => ["/"];

        public IInterceptorSubject? ResolveSubject(
            string path, PathStyle style, IInterceptorSubject? relativeTo = null) => null;
    }
}

[InterceptorSubject]
public partial class RecorderTestSubject
{
    [State]
    public partial double Temperature { get; set; }
}
