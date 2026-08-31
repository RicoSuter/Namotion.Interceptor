using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle.Acceptance;

/// <summary>
/// Shared helpers for the defect acceptance suite.
/// </summary>
internal static class AcceptanceContext
{
    public static IInterceptorSubjectContext Create() =>
        InterceptorSubjectContext.Create().WithLifecycle();

    public static IInterceptorSubjectContext CreateWithDerived() =>
        InterceptorSubjectContext.Create().WithLifecycle().WithDerivedPropertyChangeDetection();

    public static OwnershipGraph GetGraph(IInterceptorSubjectContext context) =>
        ((LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!).Graph;
}

/// <summary>
/// An enumerable that runs a hook the first time it is enumerated, which is the only user code the
/// write protocol still invokes inside a structural write on this branch. The enumeration count is
/// exposed so a test can assert its hook actually ran rather than silently pass on a window that
/// never opened.
/// </summary>
internal sealed class ReenteringEnumerable(IEnumerable<Person> items) : IEnumerable<Person>
{
    private bool _hasReentered;

    public int Enumerations { get; private set; }

    public bool HasReentered => _hasReentered;

    /// <summary>Runs on the first enumeration when it returns true; null means always re-enter.</summary>
    public Func<bool>? ShouldReenter { get; set; }

    public Action? OnReenter { get; set; }

    public IEnumerator<Person> GetEnumerator()
    {
        Enumerations++;
        if (!_hasReentered && (ShouldReenter?.Invoke() ?? true))
        {
            _hasReentered = true;
            OnReenter?.Invoke();
        }

        return items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// An enumerable that parks the enumerating thread on its first pass, so another thread can observe
/// the graph while this one holds the topology gate.
/// </summary>
internal sealed class ParkingEnumerable(Action onFirstEnumeration) : IEnumerable<Person>
{
    private int _enumerations;

    public IEnumerator<Person> GetEnumerator()
    {
        if (Interlocked.Increment(ref _enumerations) == 1)
        {
            onFirstEnumeration();
        }

        return Enumerable.Empty<Person>().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A hand-written subject that supplies a raw reader and a raw writer, the shape the branch treats
/// as trusted, whose writer stores <see cref="Substitute"/> instead of the value it was handed when
/// one is armed. <see cref="Substitute"/> is a plain field, absent from the metadata, so arming it
/// runs no write of its own.
/// </summary>
internal sealed class SubstitutingRawWriterDevice : IInterceptorSubject
{
    private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Child)] = new(
                nameof(Child),
                typeof(SubstitutingRawWriterDevice),
                [],
                static subject => ((SubstitutingRawWriterDevice)subject)._child,
                static (subject, value) => ((SubstitutingRawWriterDevice)subject).Child = (SubstitutingRawWriterDevice?)value,
                isIntercepted: true,
                isDynamic: false)
        }.ToFrozenDictionary();

    private IInterceptorExecutor? _executor;
    private SubstitutingRawWriterDevice? _child;

    public SubstitutingRawWriterDevice? Substitute;

    /// <summary>The backing field read without interception, so reading it records no dependency.</summary>
    public SubstitutingRawWriterDevice? RawChild => _child;

    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");

    public SubstitutingRawWriterDevice? Child
    {
        get => ((InterceptorExecutor)Executor).GetGeneratedPropertyValue(
            nameof(Child), static subject => ((SubstitutingRawWriterDevice)subject)._child);
        set => ((InterceptorExecutor)Executor).SetGeneratedPropertyValue(
            nameof(Child),
            value,
            static subject => ((SubstitutingRawWriterDevice)subject)._child,
            static (subject, newValue) =>
            {
                var device = (SubstitutingRawWriterDevice)subject;
                device._child = device.Substitute ?? newValue;
            });
    }
}

/// <summary>
/// The normalizing setter every consumer actually writes: it drops one proposed subject and stores
/// the rest, reordered, in a different list instance. Every stored subject was proposed, so this
/// has to stay legal, and it is the only acceptance case whose stored value must pass validation.
/// </summary>
internal sealed class ReorderingRawWriterDevice : IInterceptorSubject
{
    private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Children)] = new(
                nameof(Children),
                typeof(IReadOnlyList<ReorderingRawWriterDevice>),
                [],
                static subject => ((ReorderingRawWriterDevice)subject)._children,
                static (subject, value) => ((ReorderingRawWriterDevice)subject).Children = (IReadOnlyList<ReorderingRawWriterDevice>?)value,
                isIntercepted: true,
                isDynamic: false)
        }.ToFrozenDictionary();

    private IInterceptorExecutor? _executor;
    private IReadOnlyList<ReorderingRawWriterDevice>? _children;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<ReorderingRawWriterDevice>? RawChildren => _children;

    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");

    public IReadOnlyList<ReorderingRawWriterDevice>? Children
    {
        get => ((InterceptorExecutor)Executor).GetGeneratedPropertyValue(
            nameof(Children), static subject => ((ReorderingRawWriterDevice)subject)._children);
        set => ((InterceptorExecutor)Executor).SetGeneratedPropertyValue(
            nameof(Children),
            value,
            static subject => ((ReorderingRawWriterDevice)subject)._children,
            static (subject, newValue) => ((ReorderingRawWriterDevice)subject)._children = newValue?
                .Where(child => child.Name != "dropped")
                .OrderBy(child => child.Name)
                .ToList());
    }
}

/// <summary>
/// Carries the shapes a stored-value claim has to tell apart: an immutable array, whose equality is
/// a comparison of the underlying array reference, the same shape behind a reference-typed
/// declaration, and a value type whose equality answers about a version stamp instead of about
/// storage. Reuses the <see cref="StampedChildren"/> and <see cref="ExecutorCountingSubject"/>
/// models, but reaches the branch through a raw reader and raw writer pair.
/// </summary>
internal sealed class StoredValueClaimRawDevice : IInterceptorSubject
{
    private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(ImmutableChildren)] = new(
                nameof(ImmutableChildren),
                typeof(ImmutableArray<ExecutorCountingSubject>),
                [],
                static subject => ((StoredValueClaimRawDevice)subject)._immutableChildren,
                static (subject, value) => ((StoredValueClaimRawDevice)subject).ImmutableChildren = (ImmutableArray<ExecutorCountingSubject>)value!,
                isIntercepted: true,
                isDynamic: false),
            [nameof(ListChildren)] = new(
                nameof(ListChildren),
                typeof(IReadOnlyList<ExecutorCountingSubject>),
                [],
                static subject => ((StoredValueClaimRawDevice)subject)._listChildren,
                static (subject, value) => ((StoredValueClaimRawDevice)subject).ListChildren = (IReadOnlyList<ExecutorCountingSubject>?)value,
                isIntercepted: true,
                isDynamic: false),
            [nameof(Stamped)] = new(
                nameof(Stamped),
                typeof(StampedChildren),
                [],
                static subject => ((StoredValueClaimRawDevice)subject)._stamped,
                static (subject, value) => ((StoredValueClaimRawDevice)subject).Stamped = (StampedChildren)value!,
                isIntercepted: true,
                isDynamic: false)
        }.ToFrozenDictionary();

    private IInterceptorExecutor? _executor;
    private ImmutableArray<ExecutorCountingSubject> _immutableChildren = [];
    private IReadOnlyList<ExecutorCountingSubject>? _listChildren;
    private StampedChildren _stamped;

    public StampedChildren? StampedSubstitute;

    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");

    public ImmutableArray<ExecutorCountingSubject> ImmutableChildren
    {
        get => ((InterceptorExecutor)Executor).GetGeneratedPropertyValue(
            nameof(ImmutableChildren), static subject => ((StoredValueClaimRawDevice)subject)._immutableChildren);
        set => ((InterceptorExecutor)Executor).SetGeneratedPropertyValue(
            nameof(ImmutableChildren),
            value,
            static subject => ((StoredValueClaimRawDevice)subject)._immutableChildren,
            static (subject, newValue) => ((StoredValueClaimRawDevice)subject)._immutableChildren = newValue);
    }

    public IReadOnlyList<ExecutorCountingSubject>? ListChildren
    {
        get => ((InterceptorExecutor)Executor).GetGeneratedPropertyValue(
            nameof(ListChildren), static subject => ((StoredValueClaimRawDevice)subject)._listChildren);
        set => ((InterceptorExecutor)Executor).SetGeneratedPropertyValue(
            nameof(ListChildren),
            value,
            static subject => ((StoredValueClaimRawDevice)subject)._listChildren,
            static (subject, newValue) => ((StoredValueClaimRawDevice)subject)._listChildren = newValue);
    }

    public StampedChildren Stamped
    {
        get => ((InterceptorExecutor)Executor).GetGeneratedPropertyValue(
            nameof(Stamped), static subject => ((StoredValueClaimRawDevice)subject)._stamped);
        set => ((InterceptorExecutor)Executor).SetGeneratedPropertyValue(
            nameof(Stamped),
            value,
            static subject => ((StoredValueClaimRawDevice)subject)._stamped,
            static (subject, newValue) =>
            {
                var device = (StoredValueClaimRawDevice)subject;
                device._stamped = device.StampedSubstitute ?? newValue;
            });
    }
}
