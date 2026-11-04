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

            return (reads, tornReads, firstTornValue);
        }, "reader");

        // Act
        start.Set();
        await writer;
        var (reads, tornReads, firstTornValue) = await reader;

        // Assert
        Assert.True(reads > 0, "The reader never observed the property while the writer was running.");
        Assert.True(tornReads == 0,
            $"{tornReads} of {reads} reads were torn, the first one observed was {firstTornValue}: " +
            "the read terminal did not synchronize on the subject's SyncRoot while the write terminal did.");
    }

    private sealed class PassThroughWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
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
}
