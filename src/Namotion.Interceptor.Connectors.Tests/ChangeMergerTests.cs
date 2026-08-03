using System.Buffers;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeMergerTests
{
    [Fact]
    public void WhenTwoChangesToOnePropertyHaveNoRevision_ThenTheyCollapseByArrivalPosition()
    {
        // Arrange
        using var deduplicator = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "Value1", "Value2", revision: 0),
            CreateChange(property, "Value2", "Value3", revision: 0)
        ];

        // Act
        var deduplicated = deduplicator.Merge(changes).ToArray();

        // Assert - the batch collapses to one change keeping the oldest old value and the newest new value
        var change = Assert.Single(deduplicated);
        Assert.Equal("Value1", change.GetOldValue<string>());
        Assert.Equal("Value3", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenTheNewerCommitArrivesFirst_ThenTheSurvivorTakesItsNewValue()
    {
        // Arrange - the enqueue order is inverted against the commit order, which is the race the
        // revision fixes: enqueuing happens after the commit and outside the subject lock.
        using var deduplicator = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        var newerSource = new object();
        var newerOrigin = ChangeOrigin.FromSource(newerSource);
        var newerTimestamp = DateTimeOffset.UtcNow;
        var olderTimestamp = newerTimestamp.AddSeconds(-1);

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "NewerOld", "NewerNew", revision: 20, newerOrigin, newerTimestamp),
            CreateChange(property, "OlderOld", "OlderNew", revision: 10, ChangeOrigin.Local, olderTimestamp)
        ];

        // Act
        var deduplicated = deduplicator.Merge(changes).ToArray();

        // Assert
        var change = Assert.Single(deduplicated);
        Assert.Equal("OlderOld", change.GetOldValue<string>());
        Assert.Equal("NewerNew", change.GetNewValue<string>());
        Assert.Equal(20, change.Revision);

        // The survivor's metadata follows the highest revision, not the last arrival, which matters for a
        // consumer that keys off Origin.Source (echo suppression) or off the timestamp.
        Assert.Equal(ChangeOriginKind.FromSource, change.Origin.Kind);
        Assert.Same(newerSource, change.Origin.Source);
        Assert.Equal(newerTimestamp, change.ChangedTimestamp);
    }

    [Fact]
    public void WhenThreeCommitsArriveOutOfOrder_ThenTheSurvivorSpansTheLowestAndHighestRevision()
    {
        // Arrange
        using var deduplicator = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "Old14", "New14", revision: 14),
            CreateChange(property, "Old21", "New21", revision: 21),
            CreateChange(property, "Old7", "New7", revision: 7)
        ];

        // Act
        var deduplicated = deduplicator.Merge(changes).ToArray();

        // Assert - the baseline comes from the lowest revision, the current state from the highest
        var change = Assert.Single(deduplicated);
        Assert.Equal("Old7", change.GetOldValue<string>());
        Assert.Equal("New21", change.GetNewValue<string>());
        Assert.Equal(21, change.Revision);
    }

    [Fact]
    public void WhenARevisionZeroChangeIsNotTheLastArrival_ThenTheSurvivorKeepsTheLastArrivalNewValue()
    {
        // Arrange - a revision 0 change makes the whole property fall back to arrival position, even
        // though a later arrival carries the highest revision of the batch.
        using var deduplicator = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "ZeroOld", "ZeroNew", revision: 0),
            CreateChange(property, "MiddleOld", "MiddleNew", revision: 999),
            CreateChange(property, "LastOld", "LastNew", revision: 50)
        ];

        // Act
        var deduplicated = deduplicator.Merge(changes).ToArray();

        // Assert - the first arrival supplies the old value, the last arrival the new value
        var change = Assert.Single(deduplicated);
        Assert.Equal("ZeroOld", change.GetOldValue<string>());
        Assert.Equal("LastNew", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenARevisionZeroChangeSitsBetweenHigherRevisions_ThenTheSurvivorKeepsTheLastArrivalNewValue()
    {
        // Arrange - high revisions arrive both before and after the revision 0 change, so neither of
        // them may be promoted into the survivor once the fallback applies.
        using var deduplicator = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "EarlyHighOld", "EarlyHighNew", revision: 800),
            CreateChange(property, "ZeroOld", "ZeroNew", revision: 0),
            CreateChange(property, "LateHighOld", "LateHighNew", revision: 900),
            CreateChange(property, "LastOld", "LastNew", revision: 10)
        ];

        // Act
        var deduplicated = deduplicator.Merge(changes).ToArray();

        // Assert - the first arrival supplies the old value, the last arrival the new value
        var change = Assert.Single(deduplicated);
        Assert.Equal("EarlyHighOld", change.GetOldValue<string>());
        Assert.Equal("LastNew", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenTheRevisionZeroChangeArrivesLast_ThenTheBatchCollapsesByArrivalPosition()
    {
        // Arrange - the fallback also holds when the unordered change closes the batch
        using var deduplicator = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "FirstOld", "FirstNew", revision: 30),
            CreateChange(property, "MiddleOld", "MiddleNew", revision: 10),
            CreateChange(property, "ZeroOld", "ZeroNew", revision: 0)
        ];

        // Act
        var deduplicated = deduplicator.Merge(changes).ToArray();

        // Assert - the first arrival supplies the old value, the last arrival the new value
        var change = Assert.Single(deduplicated);
        Assert.Equal("FirstOld", change.GetOldValue<string>());
        Assert.Equal("ZeroNew", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenChangesBelongToDifferentSubjects_ThenEachPropertyIsCollapsedIndependently()
    {
        // Arrange - revisions of different subjects are not comparable, so the two properties must be
        // collapsed against their own revisions only.
        using var deduplicator = new ChangeMerger();

        var firstSubject = new Person();
        var secondSubject = new Person();

        var firstProperty = new PropertyReference(firstSubject, nameof(Person.FirstName));
        var secondProperty = new PropertyReference(secondSubject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(firstProperty, "FirstOld12", "FirstNew12", revision: 12),
            CreateChange(secondProperty, "SecondOld3", "SecondNew3", revision: 3),
            CreateChange(firstProperty, "FirstOld5", "FirstNew5", revision: 5),
            CreateChange(secondProperty, "SecondOld8", "SecondNew8", revision: 8)
        ];

        // Act
        var deduplicated = deduplicator.Merge(changes).ToArray();

        // Assert - both survive, in the arrival order of their last occurrence
        Assert.Equal(2, deduplicated.Length);

        Assert.Equal(firstProperty, deduplicated[0].Property);
        Assert.Equal("FirstOld5", deduplicated[0].GetOldValue<string>());
        Assert.Equal("FirstNew12", deduplicated[0].GetNewValue<string>());

        Assert.Equal(secondProperty, deduplicated[1].Property);
        Assert.Equal("SecondOld3", deduplicated[1].GetOldValue<string>());
        Assert.Equal("SecondNew8", deduplicated[1].GetNewValue<string>());
    }

    // Matches the deduplicator's minimum rental, so the array left in the pool lands in the bucket the
    // deduplicator rents its first buffer from.
    private const int PooledBatchSize = 256;
    private const int LargeBatchSize = 64;
    private const int SmallBatchSize = 2;

    [Fact]
    public void WhenBatchesHaveBeenReleased_ThenNeitherThePooledNorTheDeduplicatedChangesStayReferenced()
    {
        // Arrange - two sets of stale changes have to be gone: the ones the array pool handed over with
        // the buffer, which only the clear on rent removes because no flush ever writes those slots, and
        // the ones a released batch wrote, which Reset has to clear over the batch's full length rather
        // than over the length of whatever batch comes after it.
        var (deduplicator, pooledSubjects, largeBatchSubjects) = RunLargeBatchThenSmallBatch();

        // Act
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        Assert.All(pooledSubjects, subject => Assert.False(subject.IsAlive,
            "The deduplicator must release what the array pool left in the buffer it rented."));
        Assert.All(largeBatchSubjects, subject => Assert.False(subject.IsAlive,
            "Resetting a batch must clear every slot that batch filled."));

        deduplicator.Dispose();
    }

    // Not inlined, so the batches this builds are dead once it returns and the deduplicator's buffer is
    // the only thing that could still root their subjects.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ChangeMerger Deduplicator, WeakReference[] PooledSubjects, WeakReference[] LargeBatchSubjects)
        RunLargeBatchThenSmallBatch()
    {
        var pooledSubjects = LeaveChangesInThePool();

        var deduplicator = new ChangeMerger();

        var largeBatch = CreateBatch(LargeBatchSize);
        var largeBatchSubjects = ToWeakReferences(largeBatch);
        deduplicator.Merge(largeBatch);
        deduplicator.Reset();

        deduplicator.Merge(CreateBatch(SmallBatchSize));
        deduplicator.Reset();

        return (deduplicator, pooledSubjects, largeBatchSubjects);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LeaveChangesInThePool()
    {
        // ArrayPool does not clear what it takes back, so the next renter sees these changes.
        var buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(PooledBatchSize);
        var subjects = new WeakReference[buffer.Length];
        for (var index = 0; index < buffer.Length; index++)
        {
            var subject = new Person();
            buffer[index] = CreateChange(
                new PropertyReference(subject, nameof(Person.FirstName)), "Old", "New", revision: index + 1);
            subjects[index] = new WeakReference(subject);
        }

        ArrayPool<SubjectPropertyChange>.Shared.Return(buffer);
        return subjects;
    }

    private static SubjectPropertyChange[] CreateBatch(int size)
    {
        // One subject per change, so every change is the only root of a subject of its own.
        var changes = new SubjectPropertyChange[size];
        for (var index = 0; index < size; index++)
        {
            changes[index] = CreateChange(
                new PropertyReference(new Person(), nameof(Person.FirstName)), "Old", "New", revision: index + 1);
        }

        return changes;
    }

    private static WeakReference[] ToWeakReferences(SubjectPropertyChange[] changes)
    {
        var subjects = new WeakReference[changes.Length];
        for (var index = 0; index < changes.Length; index++)
        {
            subjects[index] = new WeakReference(changes[index].Property.Subject);
        }

        return subjects;
    }

    /// <summary>
    /// The disposed deduplicator is reachable: <see cref="ChangeQueueProcessor"/> releases the buffer
    /// once its Dispose wins the flush gate, and the periodic flush task can outlive that and tick
    /// again on whatever was enqueued in between. Throwing there escapes the flush, kills the periodic
    /// loop for good and leaves the queue growing unbounded, so the guards are load-bearing rather
    /// than defensive tidiness, and nothing else exercises them.
    /// </summary>
    [Fact]
    public void WhenDisposed_ThenDeduplicateIsEmptyAndResetIsANoOp()
    {
        // Arrange
        var deduplicator = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        SubjectPropertyChange[] changes = [CreateChange(property, "Value1", "Value2", revision: 1)];

        Assert.Single(deduplicator.Merge(changes).ToArray());
        deduplicator.Reset();

        // Act
        deduplicator.Dispose();

        // Assert: a flush that reaches a released buffer skips the write handler instead of throwing.
        Assert.True(deduplicator.Merge(changes).IsEmpty);

        // And the calls that would follow it in that same flush stay no-ops.
        deduplicator.Reset();
        deduplicator.Dispose();
    }

    [Fact]
    public void WhenAnEarlierFlushAlreadyDeliveredANewerCommit_ThenTheSurvivorIsSuppressed()
    {
        // Arrange: collapsing a batch cannot see across flushes, so a change enqueued late enough to
        // land in the next batch would otherwise overwrite the source with an older commit's value.
        using var deduplicator = new ChangeMerger();
        var deliveredRevisions = new DeliveredRevisionFilter();

        var subject = new Person();
        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));

        SubjectPropertyChange[] baseline =
        [
            CreateChange(firstName, "Old", "Newest", revision: 10),
            CreateChange(lastName, "Old", "Newest", revision: 10)
        ];

        Assert.Equal(2, deduplicator.Merge(baseline, deliveredRevisions).Length);
        deduplicator.Reset();

        SubjectPropertyChange[] straggler =
        [
            CreateChange(firstName, "Old", "Stale", revision: 8),
            CreateChange(lastName, "Newest", "Newer", revision: 12)
        ];

        // Act
        var deduplicated = deduplicator.Merge(straggler, deliveredRevisions).ToArray();

        // Assert: the superseded commit is dropped, the newer one still flows.
        var survivor = Assert.Single(deduplicated);
        Assert.Equal(nameof(Person.LastName), survivor.Property.Name);
        Assert.Equal("Newer", survivor.GetNewValue<string>());
    }

    /// <summary>
    /// Suppression shrinks the survivor count, and <see cref="ChangeMerger.Reset"/> only clears
    /// the prefix that count describes. Without clearing the dropped tail, those slots would keep their
    /// subjects and boxed values alive inside the pooled buffer, which outlives the batch.
    /// </summary>
    [Fact]
    public void WhenSurvivorsAreSuppressed_ThenTheDroppedSlotsHoldNoReferences()
    {
        // Arrange
        using var deduplicator = new ChangeMerger();
        var deliveredRevisions = new DeliveredRevisionFilter();

        var subject = new Person();
        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));

        deduplicator.Merge(
            [CreateChange(firstName, "Old", "Newest", revision: 10), CreateChange(lastName, "Old", "Newest", revision: 10)],
            deliveredRevisions);
        deduplicator.Reset();

        // Act: the first is superseded and dropped, the second survives.
        var deduplicated = deduplicator.Merge(
            [CreateChange(firstName, "Old", "Stale", revision: 8), CreateChange(lastName, "Newest", "Newer", revision: 12)],
            deliveredRevisions);

        // Assert
        Assert.Equal(1, deduplicated.Length);

        var buffer = GetBuffer(deduplicator);
        for (var index = deduplicated.Length; index < buffer.Length; index++)
        {
            Assert.True(buffer[index].Property.Subject is null,
                $"slot {index} past the survivor count still references a subject");
        }
    }

    private static SubjectPropertyChange[] GetBuffer(ChangeMerger deduplicator)
    {
        var field = typeof(ChangeMerger)
            .GetField("_buffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.True(field is not null, "_buffer was renamed, this test needs updating.");
        return (SubjectPropertyChange[])field!.GetValue(deduplicator)!;
    }

    private static SubjectPropertyChange CreateChange(
        PropertyReference property,
        string? oldValue,
        string? newValue,
        long revision,
        ChangeOrigin? origin = null,
        DateTimeOffset? changedTimestamp = null)
    {
        return SubjectPropertyChange.Create(
            property,
            origin ?? ChangeOrigin.Local,
            changedTimestamp ?? DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue,
            revision);
    }
}
