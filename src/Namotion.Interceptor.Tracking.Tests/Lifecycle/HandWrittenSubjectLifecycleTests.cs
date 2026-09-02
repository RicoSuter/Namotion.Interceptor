using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Hand-written structural setters cannot provide the trusted raw reader required by coordinated
/// lifecycle storage, so an attached write is rejected before its writer or graph can commit.
/// </summary>
public class HandWrittenSubjectLifecycleTests
{
    /// <summary>
    /// A subject implemented by hand, the way a consumer without the source generator writes one:
    /// every setter calls SetPropertyValue, whatever the property type.
    /// </summary>
    private sealed class HandWrittenDevice : IInterceptorSubject
    {
        private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(HandWrittenDevice),
                    [],
                    static subject => ((HandWrittenDevice)subject)._child,
                    static (subject, value) => ((HandWrittenDevice)subject).Child = (HandWrittenDevice?)value,
                    isIntercepted: true,
                    isDynamic: false),
                [nameof(Name)] = new(
                    nameof(Name),
                    typeof(string),
                    [],
                    static subject => ((HandWrittenDevice)subject)._name,
                    static (subject, value) => ((HandWrittenDevice)subject).Name = (string?)value,
                    isIntercepted: true,
                    isDynamic: false)
            }.ToFrozenDictionary();

        private IInterceptorExecutor? _executor;
        private HandWrittenDevice? _child;
        private string? _name;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException("The hand-written subject declares all its properties statically.");

        public HandWrittenDevice? Child
        {
            get => Executor.GetPropertyValue(nameof(Child), static subject => ((HandWrittenDevice)subject)._child);
            set => Executor.SetPropertyValue(nameof(Child), value, _child,
                static (subject, newValue) => ((HandWrittenDevice)subject)._child = newValue);
        }

        public string? Name
        {
            get => Executor.GetPropertyValue(nameof(Name), static subject => ((HandWrittenDevice)subject)._name);
            set => Executor.SetPropertyValue(nameof(Name), value, _name,
                static (subject, newValue) => ((HandWrittenDevice)subject)._name = newValue);
        }
    }

    [Fact]
    public void WhenAttachedHandWrittenSetterAssignsAChildSubject_ThenWriteIsRejectedBeforeCommit()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        var parent = new HandWrittenDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var child = new HandWrittenDevice();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => parent.Child = child);
        Assert.Contains("trusted raw reader", exception.Message);
        Assert.Null(parent.Child);
        Assert.Null(((IInterceptorSubject)child).TryGetContext());
        Assert.Empty(((IInterceptorSubject)child).GetParents());
    }
}
