using System.Net;
using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.Services.Discovery.MulticastDns;

/// <summary>
/// Collects the records of mDNS answers and combines them into the instances of a service type. Thread-safe.
/// </summary>
internal sealed class MulticastDnsServiceCollector
{
    private const int MaximumRecords = 4096;
    private const int MaximumBytes = 256 * 1024;
    private const int MaximumServices = 64;

    private readonly Lock _lock = new();
    private readonly List<(DnsRecord Record, IPAddress Sender)> _records = [];

    private int _bytes;
    private bool _isFull;

    /// <summary>
    /// Gets the DNS name of a service type, such as <c>_hue._tcp.local</c> for <c>_hue._tcp</c>.
    /// </summary>
    public static string GetServiceName(string serviceType)
    {
        var name = serviceType.Trim().TrimEnd('.');
        return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name : name + ".local";
    }

    /// <summary>
    /// Adds the records of one answer, up to a number of records and a size of their names and text that bound what
    /// the hosts of a network can make it hold. Once a record does not fit, it and every later record are dropped.
    /// </summary>
    public void Add(IReadOnlyList<DnsRecord> records, IPAddress sender)
    {
        lock (_lock)
        {
            foreach (var record in records)
            {
                var bytes = GetSize(record);
                if (_isFull || _records.Count == MaximumRecords || bytes > MaximumBytes - _bytes)
                {
                    _isFull = true;
                    return;
                }

                _records.Add((record, sender));
                _bytes += bytes;
            }
        }
    }

    /// <summary>
    /// Combines the collected records into the instances of <paramref name="serviceType"/>.
    /// </summary>
    public IReadOnlyList<MulticastDnsService> GetServices(string serviceType)
    {
        (DnsRecord Record, IPAddress Sender)[] records;
        lock (_lock)
        {
            records = _records.ToArray();
        }

        var serviceName = GetServiceName(serviceType);
        var suffix = "." + serviceName;
        var instances = records
            .Where(entry => entry.Record is PointerRecord pointer &&
                IsName(pointer.Name, serviceName) &&
                pointer.Target.Length > suffix.Length &&
                pointer.Target.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(entry => ((PointerRecord)entry.Record).Target, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumServices);

        var services = new List<MulticastDnsService>();
        foreach (var (record, sender) in instances)
        {
            var instanceName = ((PointerRecord)record).Target;
            var service = records.Select(entry => entry.Record).OfType<ServiceRecord>()
                .FirstOrDefault(candidate => IsName(candidate.Name, instanceName));

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in records.Select(item => item.Record).OfType<TextRecord>()
                .Where(text => IsName(text.Name, instanceName))
                .SelectMany(text => text.Entries))
            {
                var separator = entry.IndexOf('=');
                var key = separator < 0 ? entry : entry[..separator];

                // RFC 6763 section 6.4: the first occurrence of a key wins.
                if (key.Length > 0)
                {
                    properties.TryAdd(key, separator < 0 ? "" : entry[(separator + 1)..]);
                }
            }

            IPAddress[] addresses = service is null
                ? []
                : records.Select(entry => entry.Record).OfType<AddressRecord>()
                    .Where(address => IsName(address.Name, service.Target))
                    .Select(address => address.Address)
                    .Distinct()
                    .ToArray();

            services.Add(new MulticastDnsService(
                instanceName[..^suffix.Length],
                service?.Target,
                service?.Port ?? 0,
                addresses.Length > 0 ? addresses : [sender],
                properties));
        }

        return services;
    }

    /// <summary>
    /// Gets the bytes that the strings of a record hold in memory.
    /// </summary>
    private static int GetSize(DnsRecord record)
    {
        var characters = record.Name.Length + record switch
        {
            PointerRecord pointer => pointer.Target.Length,
            ServiceRecord service => service.Target.Length,
            TextRecord text => text.Entries.Sum(entry => entry.Length),
            _ => 0
        };

        return characters * sizeof(char);
    }

    private static bool IsName(string name, string expected) =>
        string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
}
