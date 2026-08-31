using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Characterization tests for the ordered lifecycle change stream. They record what the current
/// implementation publishes, in order, for the graph shapes whose outcome depends on
/// <c>OwnershipGraph.ContainsOccurrence</c>. Every one of these shapes converges to the same final graph
/// state whether or not that predicate discriminates, so only the intermediate stream distinguishes
/// them and only an ordered assertion can catch a change.
///
/// The mechanism they surround: a reconcile commits the property's new snapshot before
/// it updates any incoming edge record, so during the removal pass a recorded incoming edge can name
/// a parent whose committed value no longer contains the subject. The reachability walk rejects such
/// an edge, which is what decides whether a subject is released on the removal that orphans it or
/// several removals later.
///
/// Both callers of the walk are covered. Release asks whether a subject that lost an edge is still
/// held; anchor adoption asks whether a new edge's parent is supported independently of the
/// subject's own provisional anchor. The adoption case reaches the same window only through a
/// nested operation, and there the predicate decides the final graph state rather than only the
/// order in which it is announced.
///
/// Notification order is contract, so these tests exist to make a change to it a deliberate decision
/// rather than an accident. A failure here is not automatically a defect: it means the published
/// order moved, and the new order has to be reviewed and the expectation updated on purpose.
/// </summary>
public class OwnershipChangeStreamTests
{
    /// <summary>Records every lifecycle change in publication order as one readable line each.</summary>
    private sealed class LifecycleChangeStreamRecorder : ILifecycleHandler
    {
        private readonly List<string> _changes = [];

        public IReadOnlyList<string> Changes => _changes;

        /// <summary>
        /// Runs after the change has been recorded, so anything a nested operation publishes is
        /// appended behind the change that triggered it rather than in front of it.
        /// </summary>
        public Action<SubjectLifecycleChange>? OnChange { get; set; }

        public void Clear() => _changes.Clear();

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            var transitions = new List<string>(2);
            if (change.IsContextAttach)
            {
                transitions.Add("attached");
            }

            if (change.IsPropertyReferenceAdded)
            {
                transitions.Add("edge added");
            }

            if (change.IsPropertyReferenceRemoved)
            {
                transitions.Add("edge removed");
            }

            if (change.IsContextDetach)
            {
                transitions.Add("detached");
            }

            var edge = change.Property is { } property ? $"{property.Name}[{change.Index ?? "-"}]" : "-";
            _changes.Add($"{change.Subject} {string.Join(", ", transitions)} {edge} references={change.ReferenceCount}");
            OnChange?.Invoke(change);
        }
    }

    private static StructuralSnapshot GetCommittedSnapshot(
        IInterceptorSubjectContext context, IInterceptorSubject subject, string propertyName)
    {
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        return lifecycle.Graph.GetSnapshot(new PropertyReference(subject, propertyName));
    }

    private static IInterceptorSubjectContext CreateContext(LifecycleChangeStreamRecorder recorder)
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => recorder, _ => false);
    }

    /// <summary>
    /// Records existing behavior. Two occurrences of one subject in one collection, both dropped by
    /// one write: the first removal already orphans the subject, because the surviving record names
    /// a property whose committed value no longer contains it. The surplus edge is therefore drained
    /// from inside the release, before the detach, and the detach carries the occurrence that
    /// triggered it rather than the last one.
    /// </summary>
    [Fact]
    public void WhenEveryOrdinalOccurrenceOfOneSubjectIsRemovedInOneWrite_ThenTheSurplusEdgeDrainsBeforeTheDetach()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        root.Children = [child, child];
        recorder.Clear();

        // Act
        root.Children = [];

        // Assert
        Assert.Equal(
        [
            "C edge removed Children[0] references=0",
            "C edge removed, detached Children[1] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior. The same shape at depth three, which shows the drained occurrences
    /// arriving in ascending index order with a falling reference count while the detach still
    /// carries the highest index.
    /// </summary>
    [Fact]
    public void WhenThreeOrdinalOccurrencesAreRemovedInOneWrite_ThenBothSurplusEdgesDrainBeforeTheDetach()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        root.Children = [child, child, child];
        recorder.Clear();

        // Act
        root.Children = [];

        // Assert
        Assert.Equal(
        [
            "C edge removed Children[0] references=1",
            "C edge removed Children[1] references=0",
            "C edge removed, detached Children[2] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior for the keyed reconcile, which matches occurrences by key rather
    /// than by ordinal and walks the old occurrences in reverse. The subject is therefore orphaned
    /// on the last key in enumeration order, and the first key is drained from inside that release.
    ///
    /// Which key that is rests on <see cref="Dictionary{TKey,TValue}"/> enumerating in insertion
    /// order for a dictionary that has had no removals. That is a framework implementation detail
    /// rather than one of this codebase, so a different dictionary type here would legitimately
    /// swap the two keys below without anything in the lifecycle having changed.
    /// </summary>
    [Fact]
    public void WhenEveryKeyedOccurrenceOfOneSubjectIsRemovedInOneWrite_ThenTheDetachCarriesTheLastEnumeratedKey()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var holder = new KeyedChildrenHolder(context);
        var child = new Person { FirstName = "C" };
        holder.Children = new Dictionary<string, Person> { ["x"] = child, ["y"] = child };
        recorder.Clear();

        // Act
        holder.Children = new Dictionary<string, Person>();

        // Assert
        Assert.Equal(
        [
            "C edge removed Children[x] references=0",
            "C edge removed, detached Children[y] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior. Clearing a collection whose second entry is also held by its first
    /// entry: the walk up from that second entry reaches the sibling, and the sibling's own incoming
    /// edge is the stale one, so the sibling does not count as support. Both subjects therefore
    /// detach within this write, deepest first.
    /// </summary>
    [Fact]
    public void WhenAClearedCollectionLeavesASubjectHeldOnlyThroughASibling_ThenBothDetachInDescentOrder()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person(context) { FirstName = "R" };
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        root.Children = [first, second];
        first.Mother = second;
        recorder.Clear();

        // Act
        root.Children = [];

        // Assert
        Assert.Equal(
        [
            "B edge removed Mother[-] references=0",
            "B edge removed, detached Children[1] references=0",
            "A edge removed, detached Children[0] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior for a closed cycle that loses its only external support. Each
    /// member is held by the other, so the walk only terminates because the cycle's edges into the
    /// cleared collection no longer count. The second member is released from inside the first
    /// member's descent rather than by the reconcile loop.
    /// </summary>
    [Fact]
    public void WhenAClearedCollectionLeavesACycleUnsupported_ThenBothCycleMembersDetach()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person(context) { FirstName = "R" };
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        root.Children = [first, second];
        first.Mother = second;
        second.Mother = first;
        recorder.Clear();

        // Act
        root.Children = [];

        // Assert
        Assert.Equal(
        [
            "B edge removed Mother[-] references=0",
            "B edge removed, detached Children[1] references=0",
            "A edge removed Children[0] references=0",
            "A edge removed, detached Mother[-] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// A detach callback is drained only after the outer graph and attachment publication. A nested
    /// admission therefore cannot observe the former stale-edge window. Adding metadata to the now
    /// detached host remains supported, but it cannot publish an edge or consume the referenced
    /// subject's provisional anchor.
    /// </summary>
    [Fact]
    public void WhenANestedAdmissionRunsAfterPublication_ThenNoStaleAncestorCanAdoptTheRoot()
    {
        // Arrange: the sibling callback runs before the host's journal entry, but after the complete
        // outer graph has already been published.
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person { FirstName = "R" };
        ((IInterceptorSubject)root).AttachToContext(context);
        var host = new Person { FirstName = "H" };
        var sibling = new Person { FirstName = "S" };
        root.Children = [host, sibling];

        var provisionalRoot = new Person(context) { FirstName = "D" };

        var admissionRan = false;
        Exception? admissionException = null;
        var hostReferenceCountAtAdmission = -1;
        StructuralSnapshot? childrenSnapshotAtAdmission = null;
        recorder.OnChange = change =>
        {
            if (!change.IsContextDetach || !ReferenceEquals(change.Subject, sibling))
            {
                return;
            }

            admissionRan = true;
            hostReferenceCountAtAdmission = ((IInterceptorSubject)host).GetReferenceCount();
            childrenSnapshotAtAdmission = GetCommittedSnapshot(context, root, nameof(Person.Children));
            admissionException = Record.Exception(() => ((IInterceptorSubject)host).AddProperties(
                new SubjectPropertyMetadata(
                    "Adopted", typeof(Person), [], _ => provisionalRoot, null,
                    isIntercepted: true, isDynamic: true)));
        };

        recorder.Clear();

        // Act
        root.Children = [];

        // Assert: the nested admission ran and was accepted against the already detached host.
        Assert.True(admissionRan, "the detach callback for the released sibling never ran");
        Assert.Null(admissionException);
        Assert.Equal(0, hostReferenceCountAtAdmission);
        Assert.Empty(Assert.IsType<StructuralSnapshot>(childrenSnapshotAtAdmission).Occurrences);
        Assert.True(((IInterceptorSubject)host).Properties.ContainsKey("Adopted"));

        // No edge was published from the detached host, so the provisional root keeps its anchor.
        Assert.True(((IInterceptorSubject)provisionalRoot).Executor.AttachmentAnchor != SubjectAttachmentAnchorKind.None,
            "the provisional anchor was consumed by an ancestor edge the committed value no longer holds");
        Assert.Same(context, ((IInterceptorSubject)provisionalRoot).TryGetContext());
        Assert.Null(((IInterceptorSubject)host).TryGetContext());

        Assert.Equal(
        [
            "S edge removed, detached Children[1] references=0",
            "H edge removed, detached Children[0] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// New support is staged before obsolete support, while the journal retains the public
    /// old-edge-before-new-edge order. The retained target and its subtree never detach.
    /// </summary>
    [Fact]
    public void WhenAReparentTargetIsAlreadyOwned_ThenOldEdgeRemovalPrecedesNewEdgeAdditionWithoutDetach()
    {
        // Arrange: a three level chain hanging off the edge that is about to be replaced.
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person { FirstName = "R" };
        ((IInterceptorSubject)root).AttachToContext(context);
        var stepchild = new Person { FirstName = "S" };
        var child = new Person { FirstName = "C" };
        var grandchild = new Person { FirstName = "G" };
        root.Mother = stepchild;
        stepchild.Father = child;
        child.Father = grandchild;
        recorder.Clear();

        // Act
        root.Mother = child;

        // Assert
        Assert.Equal(
        [
            "S edge removed, detached Mother[-] references=0",
            "C edge removed Father[-] references=1",
            "C edge added Mother[-] references=2"
        ], recorder.Changes);
    }
}
