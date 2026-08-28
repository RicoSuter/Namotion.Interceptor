using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The lifecycle classifies structural properties on the declared type, not on the write's
/// compile-time type: a TProperty narrowed below a structural declared type routes scalar in the
/// executor but still runs the full structural section inside the chain, so a scalar value
/// overwriting a subject-holding property releases that subject's edges instead of leaving them
/// stale, and the callback contract applies to narrowed writes too.
/// </summary>
public class DeclaredTypeClassificationTests
{
    /// <summary>
    /// A hand-written subject with one property whose declared type is object, the narrowing
    /// shape: the generated setter path always writes the declared type, so only a hand-written
    /// SetPropertyValue call can narrow TProperty below it.
    /// </summary>
    private sealed class ObjectHolderSubject : IInterceptorSubject
    {
        private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Value)] = new(
                    nameof(Value),
                    typeof(object),
                    [],
                    static subject => ((ObjectHolderSubject)subject)._value,
                    static (subject, value) => ((ObjectHolderSubject)subject)._value = value,
                    isIntercepted: true,
                    isDynamic: false)
            };

        private IInterceptorExecutor? _executor;
        private object? _value;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException("The hand-written subject declares all its properties statically.");

        public object? Value
        {
            get => Executor.GetPropertyValue(nameof(Value), static subject => ((ObjectHolderSubject)subject)._value);
            set => Executor.SetPropertyValue(nameof(Value), value, _value,
                static (subject, newValue) => ((ObjectHolderSubject)subject)._value = newValue);
        }

        /// <summary>Writes the declared-object property with TProperty narrowed to int.</summary>
        public void SetValueNarrowed(int value)
        {
            Executor.SetPropertyValue(nameof(Value), value, 0,
                static (subject, newValue) => ((ObjectHolderSubject)subject)._value = newValue);
        }
    }

    [Fact]
    public void WhenNarrowedScalarWriteOverwritesASubjectHoldingObjectProperty_ThenTheSubjectsEdgesAreReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var holder = new ObjectHolderSubject();
        ((IInterceptorSubject)holder).AttachToContext(context);
        var person = new Person { FirstName = "P" };
        holder.Value = person;
        Assert.Same(context, person.TryGetContext());
        Assert.Equal(1, person.GetReferenceCount());

        // Act: the executor routes TProperty int scalar, but the lifecycle classifies on the
        // declared type and must run the release descent for the overwritten subject; the
        // compile-time short circuit used to skip the section entirely and leave the edges stale.
        holder.SetValueNarrowed(42);

        // Assert
        Assert.Equal(42, holder.Value);
        Assert.Null(person.TryGetContext());
        Assert.Empty(person.GetParents());
    }

    [Fact]
    public void WhenNarrowedStructuralWriteRunsInsideALifecycleCallback_ThenItThrowsContractViolation()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var holder = new ObjectHolderSubject();
        ((IInterceptorSubject)holder).AttachToContext(context);
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var person = new Person();
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, person))
            {
                holder.SetValueNarrowed(1);
            }
        };

        // Act & Assert: the declared-type classification routes the narrowed write into the
        // structural section, where the callback guard rejects it; it used to short-circuit on
        // the compile-time type and write through silently from inside the callback.
        Assert.Throws<LifecycleContractViolationException>(() => holder.Value = person);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenNarrowedAndObjectWritesRace_ThenTheSettledGraphIsConsistent()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            // Arrange
            var context = InterceptorSubjectContext.Create().WithLifecycle();
            var holder = new ObjectHolderSubject();
            ((IInterceptorSubject)holder).AttachToContext(context);
            var person = new Person();

            var objectWriter = new Thread(() => holder.Value = person);
            var narrowedWriter = new Thread(() => holder.SetValueNarrowed(iteration));
            objectWriter.IsBackground = true;
            narrowedWriter.IsBackground = true;

            // Act
            objectWriter.Start();
            narrowedWriter.Start();
            Assert.True(objectWriter.Join(TimeSpan.FromSeconds(20)), "the object write did not complete");
            Assert.True(narrowedWriter.Join(TimeSpan.FromSeconds(20)), "the narrowed write did not complete");

            // Assert: both writes ran the full structural section, so whichever committed last
            // decides the person's ownership; a narrowed write that skipped the section would
            // leave the person tracked while the backing store holds the int.
            if (holder.Value is Person storedPerson)
            {
                Assert.Same(person, storedPerson);
                Assert.Same(context, person.TryGetContext());
                Assert.Equal(1, person.GetReferenceCount());
            }
            else
            {
                Assert.Equal(iteration, holder.Value);
                Assert.Null(person.TryGetContext());
                Assert.Empty(person.GetParents());
            }
        }
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenNarrowedUnattachedWriteRacesTheSubjectsAttach_ThenNoEdgeIsSilentlyLost()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            // Arrange: the holder starts unattached, holding a subject in its declared-object
            // property, and its executor exists, so the narrowed write runs the terminal's
            // commit predicate rather than the pre-executor short circuit.
            var context = InterceptorSubjectContext.Create().WithLifecycle();
            var holder = new ObjectHolderSubject();
            var child = new Person();
            holder.Value = child;

            var attacher = new Thread(() => ((IInterceptorSubject)holder).AttachToContext(context));
            var writer = new Thread(() => holder.SetValueNarrowed(42));
            attacher.IsBackground = true;
            writer.IsBackground = true;

            // Act
            attacher.Start();
            writer.Start();
            Assert.True(attacher.Join(TimeSpan.FromSeconds(20)), "the attach did not complete");
            Assert.True(writer.Join(TimeSpan.FromSeconds(20)), "the narrowed write did not complete");

            // Assert: the narrowed write either landed before the claim and was seeded over, or
            // re-routed into the attached protocol whose reconcile released the child. In both
            // orderings the child must not stay tracked while the backing store holds the int,
            // which is exactly the silently lost edge the declared-type consult on the unattached
            // scalar arm closes.
            Assert.Equal(42, holder.Value);
            Assert.Same(context, ((IInterceptorSubject)holder).TryGetContext());
            Assert.Null(child.TryGetContext());
            Assert.Empty(child.GetParents());
        }
    }
}
