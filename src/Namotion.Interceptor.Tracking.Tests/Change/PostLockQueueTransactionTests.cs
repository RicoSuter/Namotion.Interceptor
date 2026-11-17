using System.Reactive.Concurrency;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// A change delivery queued under a lifecycle lock runs with the transaction state of its write, not with the one of
/// the drain that runs it.
/// </summary>
[Collection(DerivedPropertyTraceCollection.Name)]
public class PostLockQueueTransactionTests
{
    private static readonly TimeSpan Timeout = TestTimeouts.Default;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenObserverOfStructuralWriteBeginsATransaction_ThenChangesWrittenUnderTheLockAreDeliveredOutsideIt(bool drainsAtNestedAnchor)
    {
        // Arrange: FirstName is written while Mother attaches, so its change is queued behind Mother. The observer of
        // Mother begins a transaction, which stays ambient for the rest of the write. With drainsAtNestedAnchor it also
        // makes a structural write in a context without transactions whose handler writes under the lock, so FirstName
        // drains at that nested anchor instead of at Mother.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var otherContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Person(context);
        var mother = new Person();
        var other = new Person(otherContext);
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "h";
            }
        }));
        otherContext.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach)
            {
                other.LastName = "o";
            }
        }));

        SubjectTransaction? transaction = null;
        SubjectTransaction? transactionOfFirstName = null;
        var isFirstNameDelivered = false;
        var isFirstNameDeliveredInObserverOfMother = false;
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            switch (change.Property.Subject == root ? change.Property.Name : null)
            {
                case nameof(Person.Mother):
                    transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult();
                    if (drainsAtNestedAnchor)
                    {
                        other.Father = new Person();
                    }

                    isFirstNameDeliveredInObserverOfMother = isFirstNameDelivered;
                    break;
                case nameof(Person.FirstName):
                    isFirstNameDelivered = true;
                    transactionOfFirstName = SubjectTransaction.Current;
                    root.LastName = "fromObserver";
                    break;
            }
        });

        // Act
        SubjectTransaction? ambientAfterWrite;
        IReadOnlyList<SubjectPropertyChange> pendingChanges;
        try
        {
            root.Mother = mother;
            ambientAfterWrite = SubjectTransaction.Current;
            pendingChanges = transaction!.GetPendingChanges();
        }
        finally
        {
            transaction?.Dispose();
        }

        // Assert
        Assert.True(isFirstNameDelivered);
        Assert.Equal(drainsAtNestedAnchor, isFirstNameDeliveredInObserverOfMother);
        Assert.Null(transactionOfFirstName);
        Assert.Equal("fromObserver", root.LastName);
        Assert.Empty(pendingChanges);
        Assert.Same(transaction, ambientAfterWrite);
    }

    [Fact]
    public void WhenObserverOfStructuralWriteBeginsATransaction_ThenDerivedChangesOfChangesWrittenUnderTheLockArePublishedOutsideIt()
    {
        // Arrange: FirstName is written while Mother attaches, so FullName is recalculated at the drain, after the
        // observer of Mother began a transaction that stays ambient for the rest of the write.
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

        SubjectTransaction? transaction = null;
        SubjectTransaction? transactionOfFullName = null;
        var isFullNameDelivered = false;
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            switch (change.Property.Subject == root ? change.Property.Name : null)
            {
                case nameof(Person.Mother):
                    transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult();
                    break;
                case nameof(Person.FullName):
                    isFullNameDelivered = true;
                    transactionOfFullName = SubjectTransaction.Current;
                    root.LastName = "fromObserver";
                    break;
            }
        });

        // Act
        SubjectTransaction? ambientAfterWrite;
        IReadOnlyList<SubjectPropertyChange> pendingChanges;
        try
        {
            root.Mother = mother;
            ambientAfterWrite = SubjectTransaction.Current;
            pendingChanges = transaction!.GetPendingChanges();
        }
        finally
        {
            transaction?.Dispose();
        }

        // Assert
        Assert.True(isFullNameDelivered);
        Assert.Null(transactionOfFullName);
        Assert.Equal("fromObserver", root.LastName);
        Assert.Empty(pendingChanges);
        Assert.Same(transaction, ambientAfterWrite);
    }

    [Fact]
    public void WhenObserverOfAChangeDeliveredOutsideATransactionThrows_ThenTheWriteThrowsAndTheTransactionStaysAmbient()
    {
        // Arrange: the observer of Mother begins a transaction; an observer of the queued FirstName change throws.
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

        SubjectTransaction? transaction = null;
        SubjectTransaction? transactionOfFirstName = null;
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.Mother))
            {
                transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult();
            }
        });
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.FirstName))
            {
                transactionOfFirstName = SubjectTransaction.Current;
                throw new InvalidOperationException("observer");
            }
        });

        // Act
        Exception? writeException;
        SubjectTransaction? ambientAfterWrite;
        try
        {
            writeException = Record.Exception(() => root.Mother = mother);
            ambientAfterWrite = SubjectTransaction.Current;
        }
        finally
        {
            transaction?.Dispose();
        }

        // Assert
        Assert.Equal("observer", writeException?.Message);
        Assert.Null(transactionOfFirstName);
        Assert.NotNull(transaction);
        Assert.Same(transaction, ambientAfterWrite);
    }

    [Theory]
    [InlineData(TransactionFailureHandling.BestEffort)]
    [InlineData(TransactionFailureHandling.Rollback)]
    public void WhenObserverOfStructuralWriteCommitsAStructuralWrite_ThenAnOlderQueuedChangeIsDeliveredOutsideTheCommitAndDoesNotFailIt(
        TransactionFailureHandling failureHandling)
    {
        // Arrange: FirstName is written while Mother attaches; LastName while Father attaches in the commit's apply,
        // which is a nested anchor and also drains the older FirstName change, whose observer throws.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var root = new Person(context);
        var mother = new Person();
        var father = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "h";
            }
            else if (change.IsContextAttach && change.Subject == father)
            {
                root.LastName = "f";
            }
        }));

        Exception? commitException = null;
        var deliveries = new List<(string Name, bool IsInTransaction)>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject != root || change.Property.Name is nameof(Person.FullName) or nameof(Person.FullNameWithPrefix))
            {
                return;
            }

            deliveries.Add((change.Property.Name, SubjectTransaction.Current is not null));
            if (change.Property.Name == nameof(Person.Mother))
            {
                using var transaction = context.BeginTransactionAsync(failureHandling).GetAwaiter().GetResult();
                root.Father = father;
                commitException = Record.Exception(() => transaction.CommitAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());
            }
        });
        // A separate subscription: Rx disposes a subscription whose observer threw.
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.FirstName))
            {
                throw new InvalidOperationException("unrelated observer");
            }
        });
        using var traceCapture = new TraceCapture();

        // Act
        var writeException = Record.Exception(() => root.Mother = mother);

        // Assert
        Assert.Null(commitException);
        Assert.Null(writeException);
        Assert.Same(father, root.Father);
        Assert.Equal(
            [(nameof(Person.Mother), false), (nameof(Person.FirstName), false), (nameof(Person.Father), true), (nameof(Person.LastName), true)],
            deliveries);
        Assert.Contains("unrelated observer", traceCapture.Text);
    }

    [Fact]
    public void WhenADeliveryOfACommitRunsAfterTheCommitAndDrainsALaterChange_ThenTheExceptionOfItsObserverReachesTheWrite()
    {
        // Arrange: FirstName is written while Mother attaches, so the observer of Mother runs while its delivery is
        // pending. The commit it runs applies a plain write, whose delivery is queued behind and runs after the commit
        // with its transaction state; the later write of mother's LastName, which is made outside every transaction,
        // is queued behind it as well. The observer of the applied change makes a structural write whose handler
        // writes under the lock, a nested anchor that drains the later change, whose observer throws.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var root = new Person(context);
        var mother = new Person();
        var father = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "h";
            }
            else if (change.IsContextAttach && change.Subject == father)
            {
                root.LastName = "f";
            }
        }));

        Exception? commitException = null;
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.Mother))
            {
                using (var transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort).GetAwaiter().GetResult())
                {
                    mother.FirstName = "m";
                    commitException = Record.Exception(() => transaction.CommitAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());
                }

                mother.LastName = "later";
            }
            else if (change.Property.Subject == mother && change.Property.Name == nameof(Person.FirstName))
            {
                root.Father = father;
            }
        });
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == mother && change.Property.Name == nameof(Person.LastName))
            {
                throw new InvalidOperationException("unrelated observer");
            }
        });

        // Act
        var writeException = Record.Exception(() => root.Mother = mother);

        // Assert
        Assert.Null(commitException);
        Assert.Equal("unrelated observer", writeException?.Message);
        Assert.Same(father, root.Father);
        Assert.Equal("later", mother.LastName);
    }

    [Fact]
    public async Task WhenObserverOfAChangeWrittenUnderTheLockOfACommitApplyThrows_ThenTheCommitReportsIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var root = new Person(context);
        var father = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == father)
            {
                root.LastName = "f";
            }
        }));
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.LastName))
            {
                throw new InvalidOperationException("own observer");
            }
        });

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        root.Father = father;
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Contains(exception.Errors, error => error.Message == "own observer");
    }

    [Fact]
    public void WhenObserverOfAQueuedChangeRunsItsOwnTransaction_ThenItCommitsAndTheTransactionOfTheEarlierObserverStaysAmbient()
    {
        // Arrange: as in the first test, the observer of Mother begins a transaction (optimistic, so the context lock
        // is free). The observer of the queued FirstName change runs a transaction of its own, which needs none to be
        // ambient.
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
        SubjectTransaction? transaction = null;
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject != root)
            {
                return;
            }

            if (change.Property.Name == nameof(Person.Mother))
            {
                transaction = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic).GetAwaiter().GetResult();
            }
            else if (change.Property.Name == nameof(Person.FirstName))
            {
                using var own = context.BeginTransactionAsync(TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic).GetAwaiter().GetResult();
                root.LastName = "own";
                own.CommitAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
        });

        // Act
        SubjectTransaction? ambientAfterWrite;
        try
        {
            root.Mother = mother;
            ambientAfterWrite = SubjectTransaction.Current;
        }
        finally
        {
            transaction?.Dispose();
        }

        // Assert
        Assert.Equal("own", root.LastName);
        Assert.Same(transaction, ambientAfterWrite);
    }

    [Fact]
    public async Task WhenACommitContinuesInlineInAnObserverWithOlderQueuedChanges_ThenTheyAreDeliveredOutsideTheCommitAndDoNotFailIt()
    {
        // Arrange: the writer's task completes inline when the observer of Mother completes it, so the commit's apply
        // runs on that thread while FirstName is still queued. The apply of Father is a nested anchor that drains
        // FirstName.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var writer = new GatedWriter();
        context.AddService<ITransactionWriter>(writer);
        var root = new Person(context);
        var mother = new Person();
        var father = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "h";
            }
            else if (change.IsContextAttach && change.Subject == father)
            {
                root.LastName = "f";
            }
        }));
        int? motherThread = null;
        int? fatherThread = null;
        SubjectTransaction? transactionOfFirstName = null;
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            switch (change.Property.Subject == root ? change.Property.Name : null)
            {
                case nameof(Person.Mother):
                    motherThread = Environment.CurrentManagedThreadId;
                    writer.Complete();
                    break;
                case nameof(Person.Father):
                    fatherThread = Environment.CurrentManagedThreadId;
                    break;
                case nameof(Person.FirstName):
                    transactionOfFirstName = SubjectTransaction.Current;
                    break;
            }
        });
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.FirstName))
            {
                throw new InvalidOperationException("unrelated observer");
            }
        });
        using var traceCapture = new TraceCapture();
        var commit = Task.Run(async () =>
        {
            using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
            root.Father = father;
            await transaction.CommitAsync(CancellationToken.None);
        });
        await writer.Started.Task.WaitAsync(Timeout);

        // Act
        var writeException = Record.Exception(() => root.Mother = mother);
        var commitException = await Record.ExceptionAsync(() => commit.WaitAsync(Timeout));

        // Assert
        Assert.Equal(motherThread, fatherThread); // the scenario: the apply ran inline in the observer
        Assert.Null(writeException);
        Assert.Null(commitException);
        Assert.Same(father, root.Father);
        Assert.Null(transactionOfFirstName);
        Assert.Contains("unrelated observer", traceCapture.Text);
    }

    private sealed class GatedWriter : ITransactionWriter
    {
        // Without RunContinuationsAsynchronously: the commit continues inline in Complete's caller.
        private readonly TaskCompletionSource<SourceWriteResult> _result = new();
        private SubjectPropertyChange[] _changes = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SourceWriteResult> WriteToSourcesAsync(Memory<SubjectPropertyChange> changes, TransactionRequirement requirement, CancellationToken cancellationToken)
        {
            _changes = changes.ToArray();
            Started.TrySetResult();
            return new ValueTask<SourceWriteResult>(_result.Task);
        }

        public void Complete() => _result.TrySetResult(new SourceWriteResult(_changes, [], [], null));

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(IReadOnlyList<SubjectPropertyChange> written, object? revertState, CancellationToken cancellationToken)
            => ValueTask.FromResult(new SourceRevertResult([], []));
    }
}
