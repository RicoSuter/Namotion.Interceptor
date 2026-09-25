using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceReconcileRaceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenALocalWriteCommitsBetweenTheReconcileCheckAndItsRestore_ThenTheNewerWriteSurvives()
    {
        // Arrange: a parked local write, a load that moved the model off it, and a read interceptor that
        // commits a newer local write on another thread the first time the reconcile reads the value,
        // which is between its currency check and its restore.
        var raceWrite = new RaceWriteOnFirstReadInterceptor(nameof(Person.FirstName));
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithFullPropertyTracking()
            .WithService(() => raceWrite);

        var person = new Person(context) { FirstName = "parked" };
        var property = new PropertyReference(person, nameof(Person.FirstName));
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var parkedRevision, out _));

        var logger = new RecordingLogger();
        using var source = new TestSubjectSource(person, context, logger);
        property.SetSource(source);
        property.SetValueFromSource(source, null, null, "loaded");

        source.WriteRetryQueue.Enqueue(new[]
        {
            SubjectPropertyChange.Create<object?>(
                property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "old", "parked", parkedRevision)
        });

        raceWrite.Arm(() => person.FirstName = "user-newer");

        // Act
        await source.ReconcileRetryQueueAsync(CancellationToken.None);

        // Assert
        Assert.True(raceWrite.Fired, "The reconcile never read the property, so the race was not exercised.");
        Assert.Equal("user-newer", person.FirstName);
        Assert.Contains(logger.Warnings, message => message.Contains("0 restored") && message.Contains("1 superseded"));
    }

    [Fact]
    public async Task WhenALocalWriteCommitsInsideTheRestoreBeforeItsTerminal_ThenTheNewerWriteSurvives()
    {
        // Arrange: a parked local write, a load that moved the model off it, and a write interceptor that
        // commits a newer local write on another thread when the restore's own write enters the chain,
        // which is after every check the reconcile makes and before the terminal stores the value.
        var raceWrite = new RaceWriteBeforeTerminalInterceptor(nameof(Person.FirstName), "parked");
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithFullPropertyTracking()
            .WithService(() => raceWrite);

        var person = new Person(context) { FirstName = "parked" };
        var property = new PropertyReference(person, nameof(Person.FirstName));
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var parkedRevision, out _));

        var logger = new RecordingLogger();
        using var source = new TestSubjectSource(person, context, logger);
        property.SetSource(source);
        property.SetValueFromSource(source, null, null, "loaded");

        source.WriteRetryQueue.Enqueue(new[]
        {
            SubjectPropertyChange.Create<object?>(
                property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "old", "parked", parkedRevision)
        });

        raceWrite.Arm(() => person.FirstName = "user-newer");

        // Act
        await source.ReconcileRetryQueueAsync(CancellationToken.None);

        // Assert
        Assert.True(raceWrite.Fired, "The restore never wrote the property, so the race was not exercised.");
        Assert.Equal("user-newer", person.FirstName);
        Assert.Contains(logger.Warnings, message => message.Contains("0 restored") && message.Contains("1 superseded"));
    }

    /// <summary>
    /// Runs an armed write on another thread, once, after the first read of the named property
    /// returns, and waits for it to commit before letting the read complete.
    /// </summary>
    private sealed class RaceWriteOnFirstReadInterceptor(string propertyName) : IReadInterceptor
    {
        private Action? _write;

        public bool Fired { get; private set; }

        public void Arm(Action write) => _write = write;

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var value = next(ref context);
            if (context.Property.Name == propertyName && Interlocked.Exchange(ref _write, null) is { } write)
            {
                Fired = true;
                if (!Task.Run(write).Wait(TestTimeout))
                {
                    throw new TimeoutException("The racing write did not commit within the test timeout.");
                }
            }

            return value;
        }
    }

    /// <summary>
    /// Runs an armed write on another thread, once, when a write of the given value to the named property
    /// enters the chain, and waits for it to commit before passing that write on towards the terminal.
    /// </summary>
    private sealed class RaceWriteBeforeTerminalInterceptor(string propertyName, object triggerValue) : IWriteInterceptor
    {
        private Action? _write;

        public bool Fired { get; private set; }

        public void Arm(Action write) => _write = write;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.Property.Name == propertyName &&
                Equals(context.NewValue, triggerValue) &&
                Interlocked.Exchange(ref _write, null) is { } write)
            {
                Fired = true;
                if (!Task.Run(write).Wait(TestTimeout))
                {
                    throw new TimeoutException("The racing write did not commit within the test timeout.");
                }
            }

            next(ref context);
        }
    }
}
