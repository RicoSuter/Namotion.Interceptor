using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

public enum DeviceMode
{
    Eco,
    Normal,
    Boost
}

[InterceptorSubject]
public partial class PersistedDevice : IConfigurable
{
    private int _applyCount;

    [Configuration]
    [Range(0, 3000)]
    public partial int Speed { get; set; }

    [Configuration]
    public partial DeviceMode Mode { get; set; }

    [Configuration]
    public partial double? Ratio { get; set; }

    [Configuration]
    public partial DeviceSettings? Settings { get; set; }

    [State]
    public partial int CurrentSpeed { get; set; }

    [State]
    public partial int Counter { get; internal set; }

    public int ApplyCount => Volatile.Read(ref _applyCount);

    [Operation]
    public void Reset()
    {
        CurrentSpeed = 0;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _applyCount);
        return Task.CompletedTask;
    }
}
