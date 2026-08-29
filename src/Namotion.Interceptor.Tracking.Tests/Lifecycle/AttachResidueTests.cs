using System.Collections;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// An explicit attach discovers the prospective component, claims it, and only then seeds and
/// publishes it. Discovery reads user values, so a concurrent write can change a property after it
/// was scanned and before the root is claimed. The seed then rejects the newly installed child,
/// and the claim and the root anchor taken in between must not survive that rejection.
/// </summary>
public class AttachResidueTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>
    /// A user enumerable that runs a callback when its enumeration starts, which is where the
    /// discovery scan invokes user code.
    /// </summary>
    private sealed class ParkingEnumerable(Action onFirstEnumeration) : IEnumerable<Person>
    {
        private int _enumerations;

        public IEnumerator<Person> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerations) == 1)
            {
                onFirstEnumeration();
            }

            return Enumerable.Empty<Person>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Reproduces the finding that a rejected explicit attach leaves claim and anchor residue.
    /// The window between the discovery scan and the seed is held open artificially, by a user
    /// enumerable that parks inside the discovery scan until a second thread has installed a
    /// foreign-context child. The window itself is real; only its width is manufactured.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenSeedingRejectsAChildInstalledAfterDiscovery_ThenTheAttachLeavesNoResidue()
    {
        // Arrange: the root is scanned while it still holds an empty value, then a second thread
        // stores a child owned by another context before the claim publishes the root anchor.
        var context = CreateContext();
        var otherContext = CreateContext();
        var foreignChild = new Person { FirstName = "F" };
        ((IInterceptorSubject)foreignChild).AttachToContext(otherContext);

        var discoveryReachedTheValue = new ManualResetEventSlim(false);
        var mutationApplied = new ManualResetEventSlim(false);
        var mutationObserved = false;
        var holder = new EnumerableChildrenHolder
        {
            Children = new ParkingEnumerable(() =>
            {
                discoveryReachedTheValue.Set();
                mutationObserved = mutationApplied.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            })
        };

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            if (!discoveryReachedTheValue.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            mutationException = Record.Exception(() => holder.Children = new List<Person> { foreignChild });
            mutationApplied.Set();
        })
        {
            IsBackground = true
        };

        // Act
        mutator.Start();
        var attachException = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));
        var mutatorCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert: the race actually happened, so the repro cannot pass without it.
        Assert.True(mutatorCompleted, "the mutating thread never finished");
        Assert.True(mutationObserved, "the discovery scan never observed the concurrent mutation");
        Assert.Null(mutationException);
        Assert.NotNull(attachException);

        // The attach was rejected, so it must have published nothing.
        Assert.True(((IInterceptorSubject)holder).TryGetContext() is null,
            "the rejected attach left the root claimed by the context it was rejected from; " +
            $"seeding failed with {attachException.GetType().Name}: {attachException.Message}");
        Assert.False(((IInterceptorSubject)holder).IsAnchoredRoot(),
            "the rejected attach left the explicit root anchor published");
    }
}
