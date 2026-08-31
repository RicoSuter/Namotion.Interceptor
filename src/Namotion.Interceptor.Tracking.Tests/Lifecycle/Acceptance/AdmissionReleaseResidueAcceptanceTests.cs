using System.Collections;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle.Acceptance;

/// <summary>
/// Defect class 2: a property admission whose captured collection releases the admitting subject
/// while the batch is still committing must leave no snapshot entry behind for the released
/// subject. The enumeration of a captured value is depth-zero user code holding the topology gate
/// reentrantly, so it can run the whole write protocol against the subject being admitted.
/// </summary>
/// <remarks>
/// The original repro fired its release from a second enumeration of the captured value. This
/// branch enumerates a captured value exactly once, which its own
/// <c>WhenAdmissionCapturesACollection_ThenItsOccurrencesAreEnumeratedExactlyOnce</c> asserts, so
/// the release fires from the only enumeration there is.
/// </remarks>
public class AdmissionReleaseResidueAcceptanceTests
{
    private sealed class ReleasingEnumerable(IReadOnlyList<Person> items, Action onFirstEnumeration)
        : IEnumerable<Person>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<Person> GetEnumerator()
        {
            if (++EnumerationCount == 1)
            {
                onFirstEnumeration();
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// PASSES on this branch. Pins that when the captured collection detaches the admitting subject
    /// mid-commit, the released subject keeps no snapshot entry for either property in the batch,
    /// and neither it nor the captured child is left attached. The snapshot presence is asserted
    /// directly rather than through the snapshot value, because a committed null and no entry at all
    /// are indistinguishable by value.
    /// </summary>
    [Fact]
    public void WhenACapturedCollectionReleasesTheAdmittingSubjectMidCommit_ThenNoSnapshotEntrySurvives()
    {
        // Arrange
        var context = AcceptanceContext.Create();
        var person = new Person { FirstName = "P" };
        person.AttachToContext(context);
        var subject = (IInterceptorSubject)person;
        var child = new Person { FirstName = "C" };
        var trap = new ReleasingEnumerable([child], () => person.DetachFromContext(context));

        var batch = new[]
        {
            new SubjectPropertyMetadata(
                "Trap", typeof(IEnumerable<Person>), [], _ => trap, null,
                isIntercepted: true, isDynamic: true),
            new SubjectPropertyMetadata(
                "Extra", typeof(Person), [], _ => null, null,
                isIntercepted: true, isDynamic: true)
        };

        // Act
        Record.Exception(() => subject.AddProperties(batch));

        // Assert
        Assert.True(trap.EnumerationCount > 0,
            "the captured collection was never enumerated, so the release never ran inside the admission");
        Assert.Null(subject.TryGetContext());
        Assert.Null(child.TryGetContext());
        var graph = AcceptanceContext.GetGraph(context);
        Assert.False(graph.HasSnapshot(new PropertyReference(subject, "Trap")),
            "a snapshot entry survives for a property of the released subject");
        Assert.False(graph.HasSnapshot(new PropertyReference(subject, "Extra")),
            "a snapshot entry survives for a property of the released subject");
    }
}
