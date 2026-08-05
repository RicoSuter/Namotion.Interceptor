using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// TEMPORARY DIAGNOSTIC - delete after use.
/// Asserts quiescent consistency (local == source once writes settle) rather than "a specific value was
/// written", which is what the earlier probe got wrong.
public class ZzConvergenceProbeTests
{
    [Fact]
    public async Task StaleEchoAfterANewerLocalWrite_ThenLocalAndSourceAgree()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var source = new object();
        var written = new ConcurrentQueue<string?>();

        // The source's own state, as the write handler leaves it.
        string? sourceValue = null;

        var firstName = new PropertyReference(subject, nameof(Person.FirstName));

        using var processor = new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    if (change.Property.Name == nameof(Person.FirstName))
                    {
                        sourceValue = change.GetNewValue<string>();
                    }

                    written.Enqueue(change.GetNewValue<string>());
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMinutes(5),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        var queue = (ConcurrentQueue<SubjectPropertyChange>)typeof(ChangeQueueProcessor)
            .GetField("_changes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(processor)!;

        subject.FirstName = "V1";
        await AsyncTestHelpers.WaitUntilAsync(() => queue.Count == 1);
        await TriggerFlushAsync(processor);
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("V1"));

        // Act: a newer local write commits, then the source's echo of the OLD value lands before the
        // batch carrying the newer one is flushed.
        subject.FirstName = "V2";

        using (PendingOrigin.Set(firstName, ChangeOrigin.FromSource(source), "V1"))
        {
            subject.FirstName = "V1";
        }

        subject.LastName = "Fence";
        await AsyncTestHelpers.WaitUntilAsync(() => queue.Count == 2);
        await TriggerFlushAsync(processor);
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Fence"));

        // Assert: whatever they settle on, they must settle on the same thing.
        Assert.Equal(subject.FirstName, sourceValue);

        await cancellation.CancelAsync();
        try { await processing; } catch (OperationCanceledException) { /* expected */ }
    }

    private static async Task TriggerFlushAsync(ChangeQueueProcessor processor)
    {
        var tryFlushMethod = typeof(ChangeQueueProcessor)
            .GetMethod("TryFlushAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        var task = (ValueTask)tryFlushMethod!.Invoke(processor, [CancellationToken.None])!;
        await task;
    }
}
