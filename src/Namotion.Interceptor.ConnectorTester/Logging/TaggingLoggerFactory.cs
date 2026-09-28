using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.ConnectorTester.Logging;

/// <summary>
/// Wraps an <see cref="ILoggerFactory"/> so every created logger's category is suffixed
/// with "/{participantName}". Each participant has its own <see cref="IServiceProvider"/>
/// configured with one of these factories, so library code that resolves
/// <c>ILogger&lt;OpcUaSubjectClientSource&gt;</c> (or any other <c>ILogger&lt;T&gt;</c>) from
/// its participant SP receives a logger whose category is, for example,
/// <c>Namotion.Interceptor.OpcUa.Client.OpcUaSubjectClientSource/client</c>. The wrapped
/// providers (console + cycle log) then write the tagged category, making sibling client
/// instances distinguishable in logs without AsyncLocal flow or library changes.
/// </summary>
internal sealed class TaggingLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly string _participantName;

    public TaggingLoggerFactory(ILoggerFactory inner, string participantName)
    {
        _inner = inner;
        _participantName = participantName;
    }

    public void AddProvider(ILoggerProvider provider) =>
        // Forwarding would mutate the shared inner factory used by every participant SP
        // (and the main host), so a provider added by client-a would receive logs from
        // client-b and server too. Block this rather than silently leak the side effect.
        throw new NotSupportedException(
            "Per-participant TaggingLoggerFactory does not support adding providers; configure providers on the shared root factory instead.");

    public ILogger CreateLogger(string categoryName) =>
        _inner.CreateLogger($"{categoryName}/{_participantName}");

    public void Dispose()
    {
        // The wrapped factory is owned by the main host; not ours to dispose.
    }
}
