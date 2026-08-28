using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The write protocol claims the proposed value, then lets the terminal store, then reconciles the
/// authoritative getter output. A normalizing setter that stores something other than the proposed
/// value therefore leaves a subject attached to no context in the backing field for the length of
/// that window, where a derived getter on another thread can read it.
/// </summary>
public class NormalizingSetterDerivedRaceTests
{
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Registered after the lifecycle with no ordering attributes, which places it downstream of
    /// the lifecycle in the resolved write chain. Parking after its own <c>next</c> therefore parks
    /// after the terminal stored and before the lifecycle reconciles.
    /// </summary>
    private sealed class ParkingWriteInterceptor : IWriteInterceptor
    {
        private readonly ManualResetEventSlim _parked = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        private volatile string? _armedPropertyName;

        public void Arm(string propertyName) => _armedPropertyName = propertyName;

        public bool WaitUntilParked(TimeSpan timeout) => _parked.Wait(timeout);

        public void Release() => _release.Set();

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            var isArmed = _armedPropertyName is not null && context.Property.Name == _armedPropertyName;

            next(ref context);

            if (!isArmed)
            {
                return;
            }

            _armedPropertyName = null;
            _parked.Set();
            _release.Wait();
        }
    }

    /// <summary>
    /// Reproduces the reported defect that a derived recalculation convicts a subject a normalizing
    /// setter stored before the reconcile attached it. The store-to-reconcile window is held open
    /// artificially, by a downstream write interceptor that parks after the terminal; the window
    /// itself is real and the write is legal, so the recalculating thread must not report the
    /// transient exposure as a contract violation.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenANormalizingSetterHasStoredButNotReconciled_ThenAConcurrentRecalculationDoesNotConvictTheSubject()
    {
        // Arrange: the terminal substitutes a subject the write never proposed, so the substituted
        // subject is attached to nothing until the reconcile claims it.
        var parkingInterceptor = new ParkingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDerivedPropertyChangeDetection();
        context.AddService<IWriteInterceptor>(parkingInterceptor);

        var parent = new SubstitutingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var substitute = new SubstitutingDevice();
        parent.Substitute = substitute;

        var probe = new DerivedProjectionProbe { Projection = () => parent.Child };
        ((IInterceptorSubject)probe).AttachToContext(context);

        parkingInterceptor.Arm(nameof(SubstitutingDevice.Child));

        Exception? writeException = null;
        var writer = new Thread(
            () => writeException = Record.Exception(() => parent.Child = new SubstitutingDevice()))
        {
            IsBackground = true
        };

        // Act: the writer parks inside the window, then an unrelated scalar write on another
        // subject recalculates a derived property that projects the stored value.
        writer.Start();
        var parked = parkingInterceptor.WaitUntilParked(RendezvousTimeout);

        Exception? recalculationException = null;
        var recalculator = new Thread(
            () => recalculationException = Record.Exception(() => probe.Name = "trigger"))
        {
            IsBackground = true
        };
        recalculator.Start();
        var recalculatorCompleted = recalculator.Join(RendezvousTimeout);

        parkingInterceptor.Release();
        var writerCompleted = writer.Join(RendezvousTimeout);

        // Assert: the window was actually open while the recalculation ran, so the repro cannot
        // pass by the two writes serializing.
        Assert.True(parked, "the normalizing write never parked inside the store-to-reconcile window");
        Assert.True(recalculatorCompleted, "the recalculating thread never finished");
        Assert.True(writerCompleted, "the parked writer never finished");
        Assert.Null(writeException);

        // The scalar write is innocent and the structural write is legal, so neither may observe a
        // contract violation for a subject that is merely mid-publication.
        Assert.True(recalculationException is null,
            "an unrelated scalar write was rejected because a derived getter observed a subject " +
            "that a normalizing setter had stored but the reconcile had not attached yet: " +
            $"{recalculationException?.GetType().Name}: {recalculationException?.Message}");
        Assert.Same(context, ((IInterceptorSubject)substitute).TryGetContext());
    }
}
