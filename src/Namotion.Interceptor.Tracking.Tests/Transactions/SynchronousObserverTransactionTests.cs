using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// A transaction is not begun or committed in a synchronous change callback or under a lifecycle lock.
/// </summary>
[Collection(PerPropertySubscriptionCollection.Name)]
public class SynchronousObserverTransactionTests
{
    private const string ExpectedMessage = "A transaction cannot be begun or committed in a synchronous change observer or lifecycle callback.";

    public SynchronousObserverTransactionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSynchronousObserverOfAPlainWriteBeginsATransaction_ThenTheWriteThrowsAndNoTransactionIsAmbient()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var person = new Person(context);
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name == nameof(Person.FirstName))
            {
                using var transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult();
            }
        });

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "Jane");
        Assert.StartsWith(ExpectedMessage, exception.Message);
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public void WhenInlinePropertySubscriptionBeginsATransaction_ThenTheWriteThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var person = new Person(context);
        using var subscription = new PropertyReference(person, nameof(Person.FirstName)).SubscribeInline((in SubjectPropertyChange _) =>
        {
            using var transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult();
        });

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "Jane");
        Assert.StartsWith(ExpectedMessage, exception.Message);
    }

    [Fact]
    public async Task WhenSynchronousObserverCommitsTheAmbientTransaction_ThenTheCommitThrowsAndTheTransactionCanCommitLater()
    {
        // Arrange: the observed context has no transactions, so its write is delivered while the transaction of the
        // other context is ambient.
        var transactionContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var transactionPerson = new Person(transactionContext);
        var observedContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var observedPerson = new Person(observedContext);

        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        transactionPerson.FirstName = "Pending";
        Exception? commitException = null;
        using var subscription = observedContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(_ =>
        {
            commitException = Record.Exception(() => transaction.CommitAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());
        });

        // Act
        observedPerson.FirstName = "Jane";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.IsType<InvalidOperationException>(commitException);
        Assert.StartsWith(ExpectedMessage, commitException.Message);
        Assert.Equal("Pending", transactionPerson.FirstName);
    }

    [Theory]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.FirstName))]
    public void WhenObserverOfAStructuralWriteOrOfAQueuedChangeBeginsATransaction_ThenTheWriteThrowsAndEveryChangeIsDelivered(string observedProperty)
    {
        // Arrange: FirstName is written while Mother attaches, so its delivery is queued behind the structural write.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var root = new Person(context);
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "h";
            }
        }));

        var delivered = new List<string>();
        using var recordingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root)
            {
                delivered.Add(change.Property.Name);
            }
        });
        // A separate subscription: Rx disposes a subscription whose observer threw.
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == observedProperty)
            {
                using var transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult();
            }
        });

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => root.Mother = mother);
        Assert.StartsWith(ExpectedMessage, exception.Message);
        Assert.Null(SubjectTransaction.Current);
        Assert.Equal([nameof(Person.Mother), nameof(Person.FirstName)], delivered.Where(name => name is nameof(Person.Mother) or nameof(Person.FirstName)));
    }

    [Fact]
    public void WhenLifecycleHandlerBeginsATransaction_ThenBeginThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var root = new Person(context);
        var mother = new Person();
        Exception? beginException = null;
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                beginException = Record.Exception(() =>
                    context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult().Dispose());
            }
        }));

        // Act
        root.Mother = mother;

        // Assert
        Assert.IsType<InvalidOperationException>(beginException);
        Assert.StartsWith(ExpectedMessage, beginException.Message);
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public void WhenPropertyChangedHandlerOfADerivedPropertyBeginsATransaction_ThenBeginThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var person = new Person(context);
        Exception? beginException = null;
        ((INotifyPropertyChanged)person).PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Person.FullName))
            {
                beginException = Record.Exception(() =>
                    context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult().Dispose());
            }
        };

        // Act
        person.FirstName = "Jane";

        // Assert
        Assert.IsType<InvalidOperationException>(beginException);
        Assert.StartsWith(ExpectedMessage, beginException.Message);
    }

    [Fact]
    public async Task WhenScheduledObserverBeginsAndCommitsATransaction_ThenItSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var person = new Person(context);
        var result = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = context
            .GetPropertyChangeObservable(TaskPoolScheduler.Default)
            .Where(change => change.Property.Name == nameof(Person.FirstName))
            .Subscribe(_ => result.TrySetResult(Record.Exception(() =>
            {
                using var transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult();
                person.LastName = "Doe";
                transaction.CommitAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            })));

        // Act
        person.FirstName = "Jane";
        var exception = await result.Task.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Null(exception);
        Assert.Equal("Doe", person.LastName);
    }
}
