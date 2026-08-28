using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Component discovery reads the getters of unattached subjects with no synchronization, so a
/// value committed into the component between the discovery read and the claim would otherwise be
/// claimed never or half. The claim's verify pass rereads each newly claimed subject's structural
/// getters once, claims what discovery missed (growing its worklist), and rejects the whole
/// operation with the caller's own message when the reread finds a foreign subject, releasing
/// every claim it made.
/// </summary>
public class ClaimVerifyWorklistTests
{
    /// <summary>
    /// A hand-written subject whose structural getter returns scripted values in read order and
    /// repeats the last one, which is what a value committed into the component between the
    /// discovery read and the verify reread looks like to the attacher. The script must end on
    /// the authoritative value: later reads (seeding, reconcile) see a stable getter.
    /// </summary>
    private sealed class ScriptedChildSubject : IInterceptorSubject
    {
        private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                ["Next"] = new(
                    "Next",
                    typeof(Person),
                    [],
                    static subject => ((ScriptedChildSubject)subject).ReadNext(),
                    static (subject, value) => ((ScriptedChildSubject)subject).SetNext(value),
                    isIntercepted: true,
                    isDynamic: false)
            };

        private IInterceptorExecutor? _executor;
        private readonly List<object?> _script = [];
        private int _reads;

        public int GetterReads => _reads;

        public void Script(params object?[] values)
        {
            _script.AddRange(values);
        }

        private object? ReadNext()
        {
            var index = Math.Min(_reads, _script.Count - 1);
            _reads++;
            return index < 0 ? null : _script[index];
        }

        private void SetNext(object? value)
        {
            _script.Clear();
            _script.Add(value);
        }

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException("The hand-written subject declares all its properties statically.");
    }

    /// <summary>
    /// A hand-written attached parent whose structural property can hold any subject, so a write
    /// can propose a <see cref="ScriptedChildSubject"/> component.
    /// </summary>
    private sealed class SubjectHolderSubject : IInterceptorSubject
    {
        private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(IInterceptorSubject),
                    [],
                    static subject => ((SubjectHolderSubject)subject)._child,
                    static (subject, value) => ((SubjectHolderSubject)subject)._child = (IInterceptorSubject?)value,
                    isIntercepted: true,
                    isDynamic: false)
            };

        private IInterceptorExecutor? _executor;
        private IInterceptorSubject? _child;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException("The hand-written subject declares all its properties statically.");

        public IInterceptorSubject? Child
        {
            get => Executor.GetPropertyValue(nameof(Child), static subject => ((SubjectHolderSubject)subject)._child);
            set => Executor.SetPropertyValue(nameof(Child), value, _child,
                static (subject, newValue) => ((SubjectHolderSubject)subject)._child = newValue);
        }
    }

    [Fact]
    public void WhenAClaimedSubjectsGetterRevealsAFurtherUnattachedSubject_ThenTheVerifyPassClaimsIt()
    {
        // Arrange: the discovery walk reads null, every later read sees the late subject.
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var subject = new ScriptedChildSubject();
        var late = new Person { FirstName = "L" };
        subject.Script(null, late);

        // Act
        ((IInterceptorSubject)subject).AttachToContext(context);

        // Assert: the worklist grew by the late subject and it is fully owned, instead of the
        // attach throwing from edge publication over a half-claimed component.
        Assert.Same(context, ((IInterceptorSubject)subject).TryGetContext());
        Assert.Same(context, late.TryGetContext());
        Assert.Equal(1, late.GetReferenceCount());
        Assert.True(subject.GetterReads >= 2);
    }

    [Fact]
    public void WhenTheVerifyPassFindsAForeignSubjectDuringAWrite_ThenTheWritePathMessageIsPreservedWithZeroResidue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var foreignContext = InterceptorSubjectContext.Create().WithLifecycle();
        var parent = new SubjectHolderSubject();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var scripted = new ScriptedChildSubject();
        var foreign = new Person(foreignContext);
        scripted.Script(null, foreign);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => parent.Child = scripted);
        Assert.Contains("assigned graph", exception.Message);

        // Zero residue: the verify pass released the claim it made, nothing was published, and
        // the backing field was never written.
        Assert.Null(((IInterceptorSubject)scripted).TryGetContext());
        Assert.Null(parent.Child);
        Assert.Same(foreignContext, foreign.TryGetContext());
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        Assert.False(lifecycle.Graph.HasBaseline(new PropertyReference(scripted, "Next")));
    }

    [Fact]
    public void WhenTheVerifyPassFindsAForeignSubjectDuringAnExplicitAttach_ThenTheAttachPathMessageIsPreservedWithZeroResidue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var foreignContext = InterceptorSubjectContext.Create().WithLifecycle();
        var scripted = new ScriptedChildSubject();
        var foreign = new Person(foreignContext);
        scripted.Script(null, foreign);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((IInterceptorSubject)scripted).AttachToContext(context));
        Assert.Contains("while the attach was validating it", exception.Message);

        // Zero residue
        Assert.Null(((IInterceptorSubject)scripted).TryGetContext());
        Assert.Same(foreignContext, foreign.TryGetContext());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAnUnattachedWriteIntoTheComponentRacesTheAttach_ThenOwnershipIsAllOrNothing()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            // Arrange: the member's executor exists, so its unattached writes answer the
            // terminal's commit predicate instead of bypassing the write protocol entirely.
            var context = InterceptorSubjectContext.Create().WithLifecycle();
            var root = new Person { FirstName = "R" };
            var member = new Person { FirstName = "M" };
            _ = ((IInterceptorSubject)member).Executor;
            root.Father = member;
            var late = new Person { FirstName = "L" };

            var attacher = new Thread(() => root.AttachToContext(context));
            var writer = new Thread(() => member.Mother = late);
            attacher.IsBackground = true;
            writer.IsBackground = true;

            // Act
            attacher.Start();
            writer.Start();
            Assert.True(attacher.Join(TimeSpan.FromSeconds(20)), "the attach did not complete");
            Assert.True(writer.Join(TimeSpan.FromSeconds(20)), "the write did not complete");

            // Assert: whichever side won the race, the settled component is fully owned. The
            // write either committed before the claim and was seen by seeding (the monitor orders
            // the two), landed in the discovery window and was caught by the verify pass, or
            // re-routed into the attached protocol; a half-claimed member or an unowned late
            // subject is the residue class the worklist closes.
            Assert.Same(context, root.TryGetContext());
            Assert.Same(context, member.TryGetContext());
            Assert.Same(late, member.Mother);
            Assert.Same(context, late.TryGetContext());
            Assert.Equal(1, late.GetReferenceCount());
        }
    }
}
