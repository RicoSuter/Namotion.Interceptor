using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

[Collection(TerminalBoundaryCoordinatorCollection.Name)]
public class ConcurrentPublicationVerdictTests
{
    private static IInterceptorSubjectContext CreateContext() => InterceptorSubjectContext
        .Create()
        .WithLifecycle()
        .WithDerivedPropertyChangeDetection();

    private static ScalarTriggeredOrphanSubject ArmOrphan(IInterceptorSubjectContext context)
    {
        var subject = new ScalarTriggeredOrphanSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);
        subject.Orphan = new Person { FirstName = "orphan" };
        return subject;
    }

    [Fact]
    public void WhenAGenuineOrphanHasNoExplainingReservation_ThenItIsConvictedImmediately()
    {
        // Arrange
        var context = CreateContext();
        var subject = ArmOrphan(context);
        var evaluationsBefore = subject.EvaluationCount;

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");

        // Assert
        Assert.IsType<LifecycleContractViolationException>(exception);
        Assert.Equal(1, subject.EvaluationCount - evaluationsBefore);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenUnrelatedAdmissionIsInFlight_ThenItDoesNotExcuseAnUnreservedOrphan()
    {
        // Arrange
        var context = CreateContext();
        var subject = ArmOrphan(context);
        var unrelated = new AdmissionParkingSubject();
        ((IInterceptorSubject)unrelated).AttachToContext(context);
        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        Exception? admissionException = null;
        var registration = new SubjectPropertyRegistration(
            unrelated,
            [new SubjectPropertyMetadata(
                "Dynamic", typeof(string), [], _ => "value", null,
                isIntercepted: true, isDynamic: true)],
            properties =>
            {
                parked.Set();
                release.Wait(WriteProtocolAcceptance.RendezvousTimeout);
                unrelated.Publish(properties);
            });
        var admission = new Thread(() => admissionException = Record.Exception(
            () => unrelated.Executor.AddProperties(registration)))
        {
            IsBackground = true
        };
        admission.Start();
        var publicationReached = parked.Wait(WriteProtocolAcceptance.RendezvousTimeout);

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");
        release.Set();
        var admissionCompleted = admission.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(publicationReached, "the unrelated admission did not reach publication");
        Assert.True(admissionCompleted, "the unrelated admission did not complete");
        Assert.Null(admissionException);
        Assert.IsType<LifecycleContractViolationException>(exception);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenDerivedAliasReadsAnAdmissionPublisherValue_ThenItWaitsForReservationCompletion()
    {
        // Arrange
        var context = CreateContext();
        var root = new AdmissionParkingSubject();
        root.AttachToContext(context);
        var child = new Person { FirstName = "child" };
        var aliasObserved = new ManualResetEventSlim(false);
        var probe = new DerivedProjectionProbe
        {
            Projection = () =>
            {
                if (!root.Properties.TryGetValue("DynamicChild", out var metadata)) return null;
                aliasObserved.Set();
                return metadata.GetValue!(root);
            }
        };
        probe.AttachToContext(context);
        var publisherEntered = new ManualResetEventSlim(false);
        var releasePublisher = new ManualResetEventSlim(false);
        var registration = new SubjectPropertyRegistration(
            root,
            [new SubjectPropertyMetadata(
                "DynamicChild", typeof(Person), [], _ => child, null,
                isIntercepted: true, isDynamic: true)],
            properties =>
            {
                root.Publish(properties);
                publisherEntered.Set();
                if (!releasePublisher.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                {
                    throw new TimeoutException("Timed out waiting to finish admission publication.");
                }
            });
        Exception? admissionException = null;
        var admission = new Thread(() => admissionException = Record.Exception(
            () => root.Executor.AddProperties(registration)))
        { IsBackground = true };
        Exception? recalculationException = null;
        var recalculation = new Thread(() => recalculationException = Record.Exception(
            () => probe.Name = "trigger"))
        { IsBackground = true };

        // Act
        admission.Start();
        Assert.True(publisherEntered.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        recalculation.Start();
        Assert.True(aliasObserved.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        var waitedForReservation = SpinWait.SpinUntil(
            () => (recalculation.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
            WriteProtocolAcceptance.RendezvousTimeout);
        releasePublisher.Set();

        // Assert
        Assert.True(waitedForReservation, "the derived validation did not wait for admission completion");
        Assert.True(admission.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.True(recalculation.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.Null(admissionException);
        Assert.Null(recalculationException);
        Assert.Same(context, child.TryGetContext());
        Assert.Same(child, probe.Projected);
    }

    private sealed class AdmissionParkingSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties =
            new Dictionary<string, SubjectPropertyMetadata>();

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties =>
            Volatile.Read(ref _properties);

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            Executor.AddProperties(new SubjectPropertyRegistration(this, properties, Publish));

        internal void Publish(IReadOnlyDictionary<string, SubjectPropertyMetadata> properties) =>
            Volatile.Write(ref _properties, properties);
    }
}
