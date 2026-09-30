using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus.Polling;

/// <summary>
/// Opens sessions for a source: connects, runs the root subject's discovery, resolves and claims the mapped
/// properties and plans their reads. Not thread-safe: the source opens one session at a time.
/// </summary>
internal sealed class ModbusSessionFactory
{
    private static readonly IReadOnlySet<PropertyReference> NoExcludedProperties = new HashSet<PropertyReference>();

    private readonly IInterceptorSubject _subject;
    private readonly ModbusClientConfiguration _configuration;
    private readonly ISubjectSource _source;
    private readonly SourceOwnershipManager _ownership;
    private readonly ModbusPollingMetrics _metrics;
    private readonly ILogger _logger;

    public ModbusSessionFactory(
        IInterceptorSubject subject, ModbusClientConfiguration configuration, ISubjectSource source,
        SourceOwnershipManager ownership, ModbusPollingMetrics metrics, ILogger logger)
    {
        _subject = subject;
        _configuration = configuration;
        _source = source;
        _ownership = ownership;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Opens a session with fresh bindings. Claims every resolved property that no other source owns and whose scale
    /// factor, if any, is claimed too, and releases the previously claimed properties that are no longer resolved.
    /// </summary>
    /// <exception cref="ModbusConfigurationException">A mapping is invalid.</exception>
    public async Task<ModbusSession> OpenAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to Modbus server at {Host}:{Port}.", _configuration.Host, _configuration.Port);

        var connection = await ModbusConnection.ConnectAsync(
            _configuration.Host, _configuration.Port, _configuration.RequestTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            var excludedProperties = await DiscoverAsync(connection, cancellationToken).ConfigureAwait(false);
            var bindings = ModbusRegisterResolver.Resolve(_subject, _configuration.UnitId, excludedProperties);

            var claimedBindings = ClaimOwnership(bindings, cancellationToken);
            var poller = new ModbusPoller(claimedBindings, _configuration.MaximumRegisterGap, _source, _metrics, _logger);

            _logger.LogInformation(
                "Connected to Modbus server at {Host}:{Port}: {PropertyCount} properties in {BatchCount} read requests per poll.",
                _configuration.Host, _configuration.Port, claimedBindings.Count, poller.Batches.Count);

            return new ModbusSession(connection, poller);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Logs a failed reconnect attempt, telling an invalid mapping apart from a connection failure.
    /// </summary>
    public void LogReconnectFailure(Exception exception)
    {
        if (exception is ModbusConfigurationException)
        {
            _logger.LogError(exception, "Invalid Modbus mapping configuration; retrying in {RetryTime}.", _configuration.RetryTime);
        }
        else
        {
            _logger.LogError(exception, "Failed to reconnect to Modbus server at {Host}:{Port}.", _configuration.Host, _configuration.Port);
        }
    }

    private async Task<IReadOnlySet<PropertyReference>> DiscoverAsync(ModbusConnection connection, CancellationToken cancellationToken)
    {
        if (_subject is not IModbusDiscovery discovery)
        {
            return NoExcludedProperties;
        }

        var context = new ModbusDiscoveryContext(_source, connection, _configuration.UnitId);
        try
        {
            await discovery.DiscoverAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            context.Invalidate();
        }

        return context.ExcludedProperties;
    }

    private List<ModbusRegisterBinding> ClaimOwnership(List<ModbusRegisterBinding> bindings, CancellationToken cancellationToken)
    {
        var claimedBindings = new List<ModbusRegisterBinding>(bindings.Count);
        var claimedProperties = new HashSet<PropertyReference>(PropertyReference.Comparer);
        foreach (var binding in bindings)
        {
            if (_ownership.ClaimSource(binding.Property))
            {
                claimedBindings.Add(binding);
                claimedProperties.Add(binding.Property);
            }
            else
            {
                // A disposal cancels before it disposes the ownership, which then rejects every claim.
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogError("Property {PropertyPath} is owned by another source and is not read from Modbus.", binding.Path);
            }
        }

        DropBindingsWithUnclaimedScaleFactor(claimedBindings, claimedProperties);

        foreach (var property in _ownership.Properties)
        {
            if (!claimedProperties.Contains(property))
            {
                _ownership.ReleaseSource(property);
            }
        }

        return claimedBindings;
    }

    /// <summary>
    /// Drops the bindings whose scale factor is not claimed, as their value could never be scaled.
    /// </summary>
    private void DropBindingsWithUnclaimedScaleFactor(
        List<ModbusRegisterBinding> claimedBindings, HashSet<PropertyReference> claimedProperties)
    {
        // Repeated because a dropped binding can itself be the scale factor of another one.
        bool isDropped;
        do
        {
            isDropped = false;
            for (var index = claimedBindings.Count - 1; index >= 0; index--)
            {
                var binding = claimedBindings[index];
                if (binding.ScaleFactor is { } scaleFactor && !claimedProperties.Contains(scaleFactor.Property))
                {
                    claimedBindings.RemoveAt(index);
                    claimedProperties.Remove(binding.Property);
                    isDropped = true;
                    _logger.LogError(
                        "Property {PropertyPath} is not read from Modbus because its scale factor {ScaleFactorPath} is not.",
                        binding.Path, scaleFactor.Path);
                }
            }
        }
        while (isDropped);
    }
}
