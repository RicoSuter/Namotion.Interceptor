using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Represents a registered OPC UA client source in the dependency injection container.
/// Use <see cref="Resolve"/> to access the live <see cref="IOpcUaSubjectClientSource"/> after the host is built.
/// </summary>
public sealed class OpcUaClientRegistration
{
    private readonly string _key;

    internal OpcUaClientRegistration(string key)
    {
        _key = key;
    }

    /// <summary>
    /// Resolves the live <see cref="IOpcUaSubjectClientSource"/> for this registration from the given service provider.
    /// </summary>
    public IOpcUaSubjectClientSource Resolve(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredKeyedService<IOpcUaSubjectClientSource>(_key);
    }
}
