using System.Collections.Concurrent;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A configurable subject that is a hosted service and records every instance, start and stop, to see what
/// becomes of a device that a storage creates.
/// </summary>
[InterceptorSubject]
public partial class RecordingDevice : BackgroundService, IConfigurable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentQueue<RecordingDevice> Instances = new();

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Configuration]
    public partial string Name { get; set; }

    public RecordingDevice()
    {
        Name = string.Empty;
        Instances.Enqueue(this);
    }

    /// <summary>Every instance created since the last <see cref="Reset"/>, in the order of creation.</summary>
    public static IReadOnlyList<RecordingDevice> Created => Instances.ToArray();

    public static void Reset() => Instances.Clear();

    public bool IsStarted => _started.Task.IsCompleted;

    public bool IsStopped => _stopped.Task.IsCompleted;

    public Task WhenStartedAsync() => _started.Task.WaitAsync(Timeout);

    public Task WhenStoppedAsync() => _stopped.Task.WaitAsync(Timeout);

    public static string Serialize(string name)
        => $$"""
            {
              "$type": "{{typeof(RecordingDevice).FullName}}",
              "name": "{{name}}"
            }
            """;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped.TrySetResult();
        return base.StopAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Delay(System.Threading.Timeout.Infinite, stoppingToken);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
