using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// A root that holds its children by name, as a storage container holds its files, and records each configuration it is
/// asked to write with the role names an <see cref="Authorization"/> held at that moment.
/// </summary>
[InterceptorSubject]
public partial class RecordingConfigurationWriter : IConfigurationWriter
{
    private readonly List<(IInterceptorSubject Subject, string[] RoleNames)> _writes = [];

    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public RecordingConfigurationWriter()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    /// <summary>Whether a write reports the configuration as written.</summary>
    internal bool IsWriting { get; set; } = true;

    /// <summary>The exception a write fails with, or null.</summary>
    internal Exception? WriteException { get; set; }

    internal IReadOnlyList<(IInterceptorSubject Subject, string[] RoleNames)> Writes => _writes;

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (WriteException is { } exception)
        {
            return Task.FromException<bool>(exception);
        }

        _writes.Add((subject, subject is Authorization authorization ? [.. authorization.Roles.Keys] : []));
        return Task.FromResult(IsWriting);
    }
}
