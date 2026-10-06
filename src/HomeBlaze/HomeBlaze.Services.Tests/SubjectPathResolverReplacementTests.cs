using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests that path references follow a subject that is replaced at the same path.
/// </summary>
public class SubjectPathResolverReplacementTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenSubjectAtPathIsReplaced_ThenEveryDerivedReferenceToThePathIsRecalculated()
    {
        // Arrange
        var placeholder = new TestContainer { Name = "Placeholder" };
        var firstReference = new TestPathReference { Resolver = Resolver, Path = "/Motor1" };
        var secondReference = new TestPathReference { Resolver = Resolver, Path = "/Motor1" };
        var root = new TestReferenceRoot(Context);
        RootManager.Root = root;
        root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Motor1"] = placeholder,
            ["First"] = firstReference,
            ["Second"] = secondReference
        };

        var changes = new ConcurrentQueue<SubjectPropertyChange>();
        using var subscription = Context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Enqueue);

        // Act
        var motor = new TestContainer { Name = "Motor" };
        root.Children = new Dictionary<string, IInterceptorSubject>(root.Children) { ["Motor1"] = motor };

        // Assert
        Assert.True(HasResolvedTo(changes, firstReference, motor));
        Assert.True(HasResolvedTo(changes, secondReference, motor));
    }

    private static bool HasResolvedTo(IEnumerable<SubjectPropertyChange> changes, TestPathReference reference, IInterceptorSubject expected)
    {
        return changes.Any(change =>
            change.Property.Subject == reference &&
            change.Property.Name == nameof(TestPathReference.ResolvedSubject) &&
            ReferenceEquals(change.GetNewValue<IInterceptorSubject?>(), expected));
    }
}
