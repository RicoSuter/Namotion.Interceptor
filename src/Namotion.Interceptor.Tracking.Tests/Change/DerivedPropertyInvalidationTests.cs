using System.Reactive.Concurrency;
using Namotion.Interceptor.Interceptors;
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
    public void WhenTwoAggregatedContextsHaveDerivedDetection_ThenWriteIsAnnouncedBeforeItsDerivedChanges()
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
        Assert.Equal(3, changes.Count);
        Assert.Equal(nameof(Person.FirstName), changes[0].Property.Name);
        Assert.Equal(
            [nameof(Person.FullName), nameof(Person.FullNameWithPrefix)],
            changes.Skip(1).Select(c => c.Property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WhenChainThrowsAfterTheValueLanded_ThenDependentsArePublishedAndTheExceptionOfTheWriteWins()
    {
        // Arrange: the value lands, then an interceptor throws; an observer of a dependent throws as well.
        var thrower = new ThrowAfterLandingInterceptor();
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithService(() => thrower);
        var person = new Person(context) { LastName = "x" };
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name == nameof(Person.FullName))
            {
                throw new InvalidOperationException();
            }
        });

        // Act
        thrower.IsArmed = true;
        var exception = Record.Exception(() => person.FirstName = "b");

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Equal("b", person.FirstName);
        Assert.Equal("b x", Assert.Single(changes, c => c.Property.Name == nameof(Person.FullName)).GetNewValue<string>());
        Assert.Equal("Mr. b x", Assert.Single(changes, c => c.Property.Name == nameof(Person.FullNameWithPrefix)).GetNewValue<string>());
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

    private sealed class ThrowAfterLandingInterceptor : IWriteInterceptor
    {
        public bool IsArmed { get; set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (IsArmed && context.Property.Name == nameof(Person.FirstName))
            {
                throw new NotSupportedException();
            }
        }
    }
}
