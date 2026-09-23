using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests;

public class PropertyReadSynchronizationTests
{
    private const int Writes = 1_000_000;

    /// <summary>
    /// A context with write interceptors but no read interceptors is an ordinary configuration, and
    /// it is the one where the read terminal used to skip the subject's SyncRoot while the write
    /// terminal took it. A struct wider than a native word cannot be read atomically, so an
    /// unsynchronized read can observe half of the old value and half of the new one. The writer
    /// always stores a value whose lanes agree, so any read with disagreeing lanes is a torn read.
    /// </summary>
    [Fact]
    public async Task WhenWideStructIsReadWhileWrittenWithoutReadInterceptors_ThenNoReadIsTorn()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new PassThroughWriteInterceptor(), _ => false);

        Assert.Empty(context.GetServices<IReadInterceptor>());
        Assert.Single(context.GetServices<IWriteInterceptor>());

        var subject = new WideValueSubject(context);

        // Act
        var observation = await ReadWhileWritingAsync(subject);

        // Assert
        AssertNoTornReads(observation);
    }

    /// <summary>
    /// The chain terminal skips the lock for types the runtime reads atomically, so this pins that
    /// a wide struct still takes it when read interceptors sit in front of the read.
    /// </summary>
    [Fact]
    public async Task WhenWideStructIsReadWhileWrittenThroughReadInterceptor_ThenNoReadIsTorn()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new PassThroughReadInterceptor(), _ => false)
            .WithService(() => new PassThroughWriteInterceptor(), _ => false);

        Assert.Single(context.GetServices<IReadInterceptor>());
        Assert.Single(context.GetServices<IWriteInterceptor>());

        var subject = new WideValueSubject(context);

        // Act
        var observation = await ReadWhileWritingAsync(subject);

        // Assert
        AssertNoTornReads(observation);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void WhenPropertyIsRead_ThenSyncRootIsHeldExactlyForNonAtomicTypes(bool withReadInterceptor, bool isWideStruct, bool expectSyncRootHeld)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        if (withReadInterceptor)
        {
            context.WithService(() => new PassThroughReadInterceptor(), _ => false);
        }

        var subject = new WideValueSubject(context);
        var executor = (IInterceptorExecutor)((IInterceptorSubject)subject).Context;

        // Act
        var syncRootHeld = isWideStruct
            ? ObserveSyncRootDuringRead<WideValue>(executor, nameof(WideValueSubject.Value))
            : ObserveSyncRootDuringRead<int>(executor, nameof(WideValueSubject.Number));

        // Assert
        Assert.Equal(expectSyncRootHeld, syncRootHeld);
    }

    /// <summary>
    /// Dynamic registry properties dispatch the read with the property type widened to object, and
    /// their getter is arbitrary code that can read a field of any declared type and box it. The value
    /// behind an object read can therefore be a wide struct, so that dispatch keeps the lock.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenReadIsDispatchedWithErasedPropertyType_ThenSyncRootIsHeld(bool withReadInterceptor)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        if (withReadInterceptor)
        {
            context.WithService(() => new PassThroughReadInterceptor(), _ => false);
        }

        var subject = new WideValueSubject(context);
        var executor = (IInterceptorExecutor)((IInterceptorSubject)subject).Context;

        // Act
        var syncRootHeld = ObserveSyncRootDuringRead<object>(executor, nameof(WideValueSubject.Value));

        // Assert
        Assert.True(syncRootHeld);
    }

    private static bool ObserveSyncRootDuringRead<TProperty>(IInterceptorExecutor executor, string propertyName)
    {
        var syncRootHeld = false;
        executor.GetPropertyValue<TProperty>(propertyName, subject =>
        {
            syncRootHeld = Monitor.IsEntered(subject.SyncRoot);
            return default!;
        });

        return syncRootHeld;
    }

    private static async Task<TornReadObservation> ReadWhileWritingAsync(WideValueSubject subject)
    {
        using var start = new ManualResetEventSlim(false);

        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            start.Wait();
            for (var index = 1; index <= Writes; index++)
            {
                subject.Value = WideValue.Filled(index);
            }
        }, "writer");

        var reader = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            start.Wait();
            var reads = 0L;
            var tornReads = 0L;
            var firstTornValue = default(WideValue);
            while (!writer.IsCompleted)
            {
                var value = subject.Value;
                reads++;
                if (!value.IsUniform)
                {
                    if (tornReads == 0)
                    {
                        firstTornValue = value;
                    }

                    tornReads++;
                }
            }

            return new TornReadObservation(reads, tornReads, firstTornValue);
        }, "reader");

        start.Set();
        await writer;
        return await reader;
    }

    private static void AssertNoTornReads(TornReadObservation observation)
    {
        Assert.True(observation.Reads > 0, "The reader never observed the property while the writer was running.");
        Assert.True(observation.TornReads == 0,
            $"{observation.TornReads} of {observation.Reads} reads were torn, the first one observed was {observation.FirstTornValue}: " +
            "the read terminal did not synchronize on the subject's SyncRoot while the write terminal did.");
    }

    private sealed record TornReadObservation(long Reads, long TornReads, WideValue FirstTornValue);

    private sealed class PassThroughWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
        }
    }

    private sealed class PassThroughReadInterceptor : IReadInterceptor
    {
        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            return next(ref context);
        }
    }
}

/// <summary>
/// 128 bytes, wider than any vector register, so the copy is always several moves. Narrower values
/// are unreliable witnesses: measured against the unsynchronized read terminal, a 32 byte struct
/// never tore, and a 16 byte struct tore in some runs and not in others, so neither fails dependably.
/// </summary>
public readonly record struct WideValue(WideValueLane First, WideValueLane Second, WideValueLane Third, WideValueLane Fourth)
{
    public static WideValue Filled(long value) =>
        new(WideValueLane.Filled(value), WideValueLane.Filled(value), WideValueLane.Filled(value), WideValueLane.Filled(value));

    public bool IsUniform => this == Filled(First.A);
}

public readonly record struct WideValueLane(long A, long B, long C, long D)
{
    public static WideValueLane Filled(long value) => new(value, value, value, value);
}

[InterceptorSubject]
public partial class WideValueSubject
{
    public partial WideValue Value { get; set; }

    public partial int Number { get; set; }
}
