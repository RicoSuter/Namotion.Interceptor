using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Structural normalization is interceptor work. The coordinated raw writer is deliberately a
/// much narrower primitive: it must faithfully store its argument and must not throw.
/// </summary>
public class TerminalStoreContractTests
{
    [Fact]
    public void WhenInterceptorNormalizesStructuralValue_ThenNormalizedValueIsCommitted()
    {
        // Arrange
        var interceptor = new NormalizingChildrenInterceptor();
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        context.AddService<IWriteInterceptor>(interceptor);
        var parent = new Person(context);
        var kept = new Person { FirstName = "b" };
        var alsoKept = new Person { FirstName = "a" };
        var dropped = new Person { FirstName = "dropped" };
        interceptor.Target = parent;

        // Act
        parent.Children = [kept, alsoKept, dropped];

        // Assert
        Assert.Equal(["a", "b"], parent.Children.Select(child => child.FirstName));
        Assert.Same(context, kept.TryGetContext());
        Assert.Same(context, alsoKept.TryGetContext());
        Assert.Null(dropped.TryGetContext());
    }

    [Fact]
    public void WhenRawWriterMutatesThenThrows_ThenMutationIsOutsideCoordinatedContract()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var parent = new InvalidRawWriterSubject();
        parent.AttachToContext(context);
        var proposed = new InvalidRawWriterSubject();
        var substituted = new InvalidRawWriterSubject();
        parent.Substitute = substituted;
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var property = new PropertyReference(parent, nameof(InvalidRawWriterSubject.Child));
        var snapshot = lifecycle.Graph.GetSnapshot(property);

        // Act
        var exception = Record.Exception(() => parent.Child = proposed);

        // Assert: the terminal primitive violated both promises. Core cannot replay an arbitrary
        // writer to restore the field, but it has not published a graph transaction for it.
        Assert.IsType<InvalidRawWriterException>(exception);
        Assert.Same(substituted, parent.Child);
        Assert.Same(snapshot, lifecycle.Graph.GetSnapshot(property));
        Assert.Null(proposed.TryGetContext());
        Assert.Null(substituted.TryGetContext());
    }

    private sealed class NormalizingChildrenInterceptor : IWriteInterceptor
    {
        public Person? Target { get; set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, Target) &&
                context.Property.Name == nameof(Person.Children) &&
                context.NewValue is Person[] children)
            {
                context.NewValue = (TProperty)(object)children
                    .Where(child => child.FirstName != "dropped")
                    .OrderBy(child => child.FirstName)
                    .ToArray();
            }

            next(ref context);
        }
    }

    private sealed class InvalidRawWriterSubject : IInterceptorSubject
    {
        private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(InvalidRawWriterSubject),
                    [],
                    static subject => ((InvalidRawWriterSubject)subject)._child,
                    static (subject, value) => ((InvalidRawWriterSubject)subject).Child = (InvalidRawWriterSubject?)value,
                    isIntercepted: true,
                    isDynamic: false)
            }.ToFrozenDictionary();

        private IInterceptorExecutor? _executor;
        private InvalidRawWriterSubject? _child;

        public InvalidRawWriterSubject? Substitute { get; set; }

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException();

        public InvalidRawWriterSubject? Child
        {
            get => ((InterceptorExecutor)Executor).GetGeneratedPropertyValue(
                nameof(Child), static subject => ((InvalidRawWriterSubject)subject)._child);
            set => ((InterceptorExecutor)Executor).SetGeneratedPropertyValue(
                nameof(Child),
                value,
                static subject => ((InvalidRawWriterSubject)subject)._child,
                static (subject, _) =>
                {
                    var instance = (InvalidRawWriterSubject)subject;
                    instance._child = instance.Substitute;
                    throw new InvalidRawWriterException();
                });
        }
    }

    private sealed class InvalidRawWriterException : Exception;
}
