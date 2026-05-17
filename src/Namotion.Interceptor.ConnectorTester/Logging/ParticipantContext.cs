using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.ConnectorTester.Logging;

/// <summary>
/// AsyncLocal-flowed participant name read by CycleLogger so multi-instance client sources
/// (e.g. server / client-a / client-b) log under distinguishable categories without requiring
/// per-call hooks or new configuration surface in the library's client source classes.
///
/// Set once at participant startup via <see cref="Setter"/>, which runs as an
/// <see cref="IHostedService"/> registered just before the participant's client source. Because
/// the host starts services sequentially, the AsyncLocal value flows into the calling
/// ExecutionContext for the rest of the host's startup loop, and any background tasks the
/// client source spawns in its own StartAsync inherit it at task-spawn time. Background tasks
/// from different participants therefore see different values even though the value is set on
/// shared static state.
/// </summary>
internal static class ParticipantContext
{
    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current => _current.Value;

    public sealed class Setter : IHostedService
    {
        private readonly string _name;

        public Setter(string name) => _name = name;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _current.Value = _name;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
