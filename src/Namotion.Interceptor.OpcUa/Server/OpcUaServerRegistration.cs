using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Represents a registered OPC UA server in the dependency injection container.
/// Use <see cref="Resolve"/> to access the live <see cref="IOpcUaSubjectServer"/> after the host is built.
/// </summary>
public sealed class OpcUaServerRegistration
{
    private readonly string _key;

    internal OpcUaServerRegistration(string key)
    {
        _key = key;
    }

    /// <summary>
    /// Resolves the live <see cref="IOpcUaSubjectServer"/> for this registration from the given service provider.
    /// </summary>
    public IOpcUaSubjectServer Resolve(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredKeyedService<IOpcUaSubjectServer>(_key);
    }
}
