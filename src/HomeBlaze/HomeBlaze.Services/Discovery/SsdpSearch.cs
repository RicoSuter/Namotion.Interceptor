using System.Net.Sockets;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;
using Rssdp;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Searches over SSDP on every IPv4 adapter.
/// </summary>
internal sealed class SsdpSearch : ISsdpSearch
{
    private static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(3);

    // Bounds what a hostile or malfunctioning host can make one search return.
    private const int MaximumResponses = 64;

    private readonly ILogger<SsdpSearch> _logger;

    public SsdpSearch(ILogger<SsdpSearch> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken)
    {
        try
        {
            using var locator = new AggregateSsdpDeviceLocator(includeIpv4: true, includeIpv6: false, adapterFilter: null, logger: null);
            var devices = await locator.SearchAsync(searchTarget, SearchWindow, cancellationToken);
            return ToResponses(devices, searchTarget);
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException or AggregateException)
        {
            // No adapter can search, which is a normal case in a container.
            _logger.LogDebug(exception, "The SSDP search for {SearchTarget} failed.", searchTarget);
            return [];
        }
    }

    internal static IReadOnlyList<SsdpResponse> ToResponses(IEnumerable<DiscoveredSsdpDevice> devices, string searchTarget)
    {
        var responses = new List<SsdpResponse>();
        foreach (var device in devices)
        {
            // Other devices can answer a search with any notification type.
            if (device.NotificationType == searchTarget && device.DescriptionLocation is { IsAbsoluteUri: true } location)
            {
                responses.Add(new SsdpResponse(location, device.Usn, device.NotificationType));
                if (responses.Count == MaximumResponses)
                {
                    break;
                }
            }
        }

        return responses;
    }
}
