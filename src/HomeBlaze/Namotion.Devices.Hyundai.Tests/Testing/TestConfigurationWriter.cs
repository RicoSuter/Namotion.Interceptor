using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Hyundai.Tests.Testing;

/// <summary>A storage stand-in that holds the account and records every write with the requests sent before it.</summary>
[InterceptorSubject]
internal partial class TestConfigurationWriter : IConfigurationWriter
{
    private readonly List<(string Tokens, int RequestCount)> _writes = [];

    public partial BluelinkAccount? Account { get; set; }

    internal FakeHttpMessageHandler? Handler { get; init; }

    public bool ShouldFail { get; set; }

    /// <summary>What the write returns; false like a storage that is reconnecting.</summary>
    public bool IsWriteAccepted { get; set; } = true;

    /// <summary>The stored tokens of each write with the number of HTTP requests sent before it.</summary>
    internal IReadOnlyList<(string Tokens, int RequestCount)> Writes => _writes;

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        _writes.Add((((BluelinkAccount)subject).Tokens, Handler?.Requests.Count ?? 0));
        return ShouldFail ? throw new IOException("The disk is full.") : Task.FromResult(IsWriteAccepted);
    }
}
