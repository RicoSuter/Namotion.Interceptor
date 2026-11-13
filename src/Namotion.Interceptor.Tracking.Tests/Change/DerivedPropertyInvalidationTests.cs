using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyInvalidationTests
{
    [Fact]
    public void WhenTwoAggregatedContextsHaveDerivedDetection_ThenDerivedChangeIsPublishedOnce()
    {
        // Arrange
        static IInterceptorSubjectContext Create() => InterceptorSubjectContext.Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions()
            .WithContextInheritance();
        var parent = Create();
        var child = Create();
        child.AddFallbackContext(parent);
        var person = new Person(child) { FirstName = "a", LastName = "x" };
        var changes = new List<SubjectPropertyChange>();
        parent.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        person.FirstName = "b";

        // Assert
        Assert.Single(changes, c => c.Property.Name == nameof(Person.FullName));
        Assert.Single(changes, c => c.Property.Name == nameof(Person.FullNameWithPrefix));
    }

    [Fact]
    public void WhenSourceWithoutContextIsWritten_ThenAttachedDerivedPropertyIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var source = new Person { FirstName = "a" };
        var reader = new FieldReadingSubject { Source = source };
        var root = new ReaderRoot(context) { Reader = reader };
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        source.FirstName = "b";

        // Assert
        var change = Assert.Single(changes, c => c.Property.Subject == reader && c.Property.Name == nameof(FieldReadingSubject.SourceName));
        Assert.Equal("b", change.GetNewValue<string?>());
        Assert.Same(reader, root.Reader);
        Assert.True(SubjectLifecycleAttachments.IsAttached(reader));
    }

    [Fact]
    public async Task WhenTransactionsAndDerivedDetectionRunWithoutChangeInterceptor_ThenCapturedStructuralWriteAttachesNothing()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithDerivedPropertyChangeDetection().WithTransactions();
        var root = new Person(context);
        var child = new Person();

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            root.Mother = child;
        }

        // Assert
        Assert.Equal(0, child.GetReferenceCount());
    }
}
