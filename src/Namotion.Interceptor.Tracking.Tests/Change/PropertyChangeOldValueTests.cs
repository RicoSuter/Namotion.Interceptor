using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyChangeOldValueTests
{
    [Fact]
    public async Task WhenAnotherWriteCommitsWhileAWriteIsInFlight_ThenThePublishedOldValueIsTheValueItOverwrote()
    {
        // Arrange: the first write passes the equality check, which reads the current value, then parks
        // before the terminal commit while a second write to the same property commits.
        var gate = new GatedWriteInterceptor(parkedValue: "first");
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.WithService(() => gate);
        var person = new Person(context) { FirstName = "initial" };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                lock (changes)
                {
                    changes.Add(change);
                }
            });

        // The write parks in the interceptor chain, so it must not wait for a pool thread.
        var first = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { person.FirstName = "first"; }, "first");
        Assert.True(gate.Parked.Wait(TimeSpan.FromSeconds(10)), "The first write did not reach the gate.");

        // Act
        person.FirstName = "second";
        gate.Release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the second write committed first, so the first write overwrote "second", not the
        // "initial" it observed before parking.
        var firstNameChanges = changes.Where(change => change.Property.Name == nameof(Person.FirstName)).ToList();
        var firstChange = Assert.Single(firstNameChanges, change => change.GetNewValue<string?>() == "first");
        var secondChange = Assert.Single(firstNameChanges, change => change.GetNewValue<string?>() == "second");
        Assert.Equal("initial", secondChange.GetOldValue<string?>());
        Assert.Equal("second", firstChange.GetOldValue<string?>());
        Assert.True(firstChange.Revision > secondChange.Revision,
            "The parked write must have committed after the write that overtook it.");
    }

    // Parks the write carrying the given value between the tracking interceptors and the terminal
    // commit; every other write passes through.
    [RunsAfter(typeof(PropertyChangeInterceptor))]
    private sealed class GatedWriteInterceptor(string parkedValue) : IWriteInterceptor
    {
        public ManualResetEventSlim Parked { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (Equals(context.NewValue, parkedValue))
            {
                Parked.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the parked write within 10 seconds.");
                }
            }

            next(ref context);
        }
    }
}
