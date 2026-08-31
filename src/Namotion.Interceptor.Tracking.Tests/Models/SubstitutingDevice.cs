using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>A hand-written subject exposing a faithful raw structural storage pair.</summary>
internal sealed class SubstitutingDevice : IInterceptorSubject
{
    private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Child)] = new(
                nameof(Child),
                typeof(SubstitutingDevice),
                [],
                static subject => ((SubstitutingDevice)subject)._child,
                static (subject, value) => ((SubstitutingDevice)subject).Child = (SubstitutingDevice?)value,
                isIntercepted: true,
                isDynamic: false)
        }.ToFrozenDictionary();

    private IInterceptorExecutor? _executor;
    private SubstitutingDevice? _child;

    /// <summary>Runs after the faithful raw assignment and before graph publication.</summary>
    public Action<SubstitutingDevice?>? OnRawValueWritten;

    /// <summary>
    /// The stored value read straight off the backing field, the way a device exposes state it also
    /// keeps in an intercepted property. Reading it records no dependency, because nothing
    /// intercepts it, which is what a design that infers a recalculation from the dependency set
    /// cannot see.
    /// </summary>
    public SubstitutingDevice? ChildWithoutInterception => _child;

    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");

    public SubstitutingDevice? Child
    {
        get => ((InterceptorExecutor)Executor).GetGeneratedPropertyValue(
            nameof(Child), static subject => ((SubstitutingDevice)subject)._child);
        set => ((InterceptorExecutor)Executor).SetGeneratedPropertyValue(
            nameof(Child), value,
            static subject => ((SubstitutingDevice)subject)._child,
            static (subject, newValue) =>
            {
                var device = (SubstitutingDevice)subject;
                device._child = newValue;
                device.OnRawValueWritten?.Invoke(newValue);
            });
    }
}
