using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A hand-written subject whose terminal substitutes <see cref="Substitute"/> for the proposed
/// value when one is armed. <see cref="Substitute"/> is a plain auto-property, absent from the
/// metadata and therefore not intercepted, so arming it triggers no write of its own.
/// </summary>
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

    public SubstitutingDevice? Substitute { get; set; }

    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");

    public SubstitutingDevice? Child
    {
        get => Executor.GetPropertyValue(nameof(Child), static subject => ((SubstitutingDevice)subject)._child);
        set => Executor.SetPropertyValue(nameof(Child), value, _child,
            static (subject, newValue) =>
            {
                var device = (SubstitutingDevice)subject;
                device._child = device.Substitute ?? newValue;
            });
    }
}
