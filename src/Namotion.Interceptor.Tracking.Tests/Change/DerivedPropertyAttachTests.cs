using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyAttachTests
{
    [Fact]
    public void WhenASourceIsWrittenWhileItsSubjectAttachesBeforeTheDerivedPropertyIsAttached_ThenTheDerivedChangeIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person();
        context.AddService<ILifecycleHandler>(new AttachingHook(child, () => child.FirstName = "Ann"));

        // Act
        parent.Father = child;

        // Assert: the structural write is announced first, then the changes its attach caused.
        Assert.Equal(
        [
            "Father: null -> Ann",
            "FirstName: null -> Ann",
            "FullName: NA -> Ann",
            "FullNameWithPrefix: Mr. NA -> Mr. Ann"
        ], changes.Select(Describe).ToArray());
    }

    [Fact]
    public void WhenAnotherThreadWritesASourceWhileItsSubjectAttaches_ThenTheDerivedChangeIsPublished()
    {
        // Arrange: a write of another thread does not wait for the attaching thread's lifecycle lock.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            lock (changes)
            {
                changes.Add(change);
            }
        });
        var parent = new Person(context);
        var child = new Person();
        var isWritten = false;
        context.AddService<ILifecycleHandler>(new AttachingHook(child, () =>
            isWritten = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => child.FirstName = "Ann").Wait(TestTimeouts.Default)));

        // Act
        parent.Father = child;

        // Assert
        Assert.True(isWritten);
        Assert.Contains("FullName: NA -> Ann", changes.Select(Describe));
        Assert.Contains("FullNameWithPrefix: Mr. NA -> Mr. Ann", changes.Select(Describe));
    }

    [Fact]
    public void WhenADerivedValueDoesNotChangeWhileItsSubjectAttaches_ThenTheAttachPublishesNoDerivedChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person { FirstName = "Ann" };

        // Act
        parent.Father = child;

        // Assert
        Assert.Equal(["Father: null -> Ann"], changes.Select(Describe).ToArray());
        Assert.Equal("Ann", child.FullName);
    }

    [Fact]
    public void WhenASourceWrittenWhileItsSubjectAttachesChangesTheDerivedValueBack_ThenTheAttachPublishesNoDerivedChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person { FirstName = "Ann" };
        context.AddService<ILifecycleHandler>(new AttachingHook(child, () =>
        {
            child.FirstName = "Bob";
            child.FirstName = "Ann";
        }));

        // Act
        parent.Father = child;

        // Assert
        Assert.Equal(
        [
            "Father: null -> Ann",
            "FirstName: Ann -> Bob",
            "FirstName: Bob -> Ann"
        ], changes.Select(Describe).ToArray());
    }

    [Fact]
    public void WhenASourceIsWrittenAfterTheDerivedValueWasPublishedOnAttach_ThenTheNextChangeStartsFromThePublishedValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person();
        context.AddService<ILifecycleHandler>(new AttachingHook(child, () => child.FirstName = "Ann"));
        parent.Father = child;
        changes.Clear();

        // Act
        child.LastName = "Smith";

        // Assert
        Assert.Contains("FullName: Ann -> Ann Smith", changes.Select(Describe));
    }

    private static string Describe(SubjectPropertyChange change) =>
        $"{change.Property.Name}: {change.GetOldValue<object?>() ?? "null"} -> {change.GetNewValue<object?>() ?? "null"}";

    /// <summary>
    /// Runs an action once when the subject attaches, after context inheritance published its writes and before its
    /// properties are attached.
    /// </summary>
    [RunsAfter(typeof(ContextInheritanceHandler))]
    private sealed class AttachingHook(IInterceptorSubject subject, Action onAttach) : ILifecycleHandler
    {
        private bool _hasRun;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, subject) && !_hasRun)
            {
                _hasRun = true;
                onAttach();
            }
        }
    }
}
