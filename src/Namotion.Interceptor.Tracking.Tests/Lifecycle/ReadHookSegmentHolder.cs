using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A hand-written subject whose structural <see cref="Children"/> property holds a boxed
/// <see cref="ArraySegment{T}"/> and whose getter runs <see cref="OnRead"/> on every read, including
/// the reads the lifecycle makes while it seeds and reconciles the property.
/// </summary>
public sealed class ReadHookSegmentHolder : IInterceptorSubject
{
    private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Children)] = new(nameof(Children), typeof(ArraySegment<PayloadNode>), [],
                static subject => ((ReadHookSegmentHolder)subject).ReadChildren(),
                static (subject, value) => ((ReadHookSegmentHolder)subject).Children = (ArraySegment<PayloadNode>)value!,
                isIntercepted: true, isDynamic: false)
        };
    private IInterceptorExecutor? _executor;
    private ArraySegment<PayloadNode> _children = new([]);
    public Action? OnRead { get; set; }
    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;
    public ArraySegment<PayloadNode> Children
    {
        get => Executor.GetPropertyValue(nameof(Children), static subject =>
            {
                var owner = (ReadHookSegmentHolder)subject;
                owner.OnRead?.Invoke();
                return owner._children;
            });
        set => Executor.SetPropertyValue(nameof(Children), value, _children,
            static (subject, newValue) => ((ReadHookSegmentHolder)subject)._children = newValue);
    }
    private ArraySegment<PayloadNode> ReadChildren() { OnRead?.Invoke(); return _children; }
    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
}
