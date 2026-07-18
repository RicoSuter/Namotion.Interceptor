using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)] // serialized; see Task 4
public class PerPropertySubscriptionTests
{
    public PerPropertySubscriptionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenPropertyChanges_ThenListenerIsInvokedByRef()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        string? captured = null;
        using var subscription = property.Subscribe((in SubjectPropertyChange c) => captured = c.GetNewValue<string?>());

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", captured);
    }

    [Fact]
    public void WhenOtherPropertyChanges_ThenListenerIsNotInvoked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var invoked = false;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => invoked = true);

        // Act
        person.LastName = "Doe";

        // Assert
        Assert.False(invoked);
    }

    [Fact]
    public void WhenSubscriptionDisposed_ThenListenerStopsAndCountReturnsToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var count = 0;
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => count++);

        // Act
        person.FirstName = "John";
        subscription.Dispose();
        person.FirstName = "Jane";

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenUnknownMember_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new PropertyReference(person, "DoesNotExist").Subscribe((in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public async Task WhenSubscriptionInstalledWhileWriteInFlight_ThenWriteDeliversWithOldValueEqualToNewValue()
    {
        // Arrange: the blocker sits AFTER PropertyChangeInterceptor in the chain, so the writer
        // passes the idle pre-commit gate (count zero, no old value captured), then parks before
        // the commit. Pins post-commit listener resolution and the DispatchLateListeners caveat.
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);
        var received = new List<(string OldValue, string NewValue)>();

        var writer = Task.Run(() => person.FirstName = "John");
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act: install while the write is in flight (post-gate, pre-commit), then release the commit.
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) =>
                received.Add((change.GetOldValue<string>(), change.GetNewValue<string>())));
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the racing write was delivered (commit happened after the install); the idle-entry
        // path could not capture the pre-write value, so OldValue equals NewValue (documented caveat).
        var change = Assert.Single(received);
        Assert.Equal("John", change.NewValue);
        Assert.Equal("John", change.OldValue);
    }

    [Fact]
    public void WhenWriteCommitsBeforeInstall_ThenSubscriberSeesCommittedValueByReading()
    {
        // Arrange: the write completes fully before the subscription exists.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        person.FirstName = "John";
        var hits = 0;

        // Act: a write that committed before the install is not delivered; the documented recovery
        // is reading the property after Subscribe returns.
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => hits++);

        // Assert
        Assert.Equal(0, hits);
        Assert.Equal("John", person.FirstName);
    }

    [RunsAfter(typeof(PropertyChangeInterceptor))]
    private sealed class BlockingWriteInterceptor : IWriteInterceptor
    {
        public ManualResetEventSlim EnteredInnerChain { get; } = new(false);
        public ManualResetEventSlim ProceedWithCommit { get; } = new(false);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            EnteredInnerChain.Set();
            if (!ProceedWithCommit.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the blocked write within 10 seconds.");
            }

            next(ref context);
        }
    }
}
