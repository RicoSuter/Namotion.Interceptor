using System.Collections.Concurrent;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

/// <summary>
/// A configuration writer that records what it was asked to write, found as the first parent of the devices it holds.
/// </summary>
[InterceptorSubject]
public partial class RecordingConfigurationStore : IConfigurationWriter
{
    private readonly ConcurrentQueue<IInterceptorSubject> _writtenSubjects = new();

    public partial PersistedDevice? Device { get; set; }

    public partial PersistedDevice? OtherDevice { get; set; }

    /// <summary>The result of every write, false to act as a writer that stores no configuration of the subject.</summary>
    public bool WritesConfiguration { get; set; } = true;

    public IReadOnlyCollection<IInterceptorSubject> WrittenSubjects => _writtenSubjects;

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        _writtenSubjects.Enqueue(subject);
        return Task.FromResult(WritesConfiguration);
    }
}
