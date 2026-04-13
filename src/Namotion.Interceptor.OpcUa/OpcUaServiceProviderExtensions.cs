using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaServiceProviderExtensions
{
    /// <summary>
    /// Gets the OPC UA client source instance for the given registration.
    /// </summary>
    public static IOpcUaSubjectClientSource GetOpcUaSubjectClientSource(
        this IServiceProvider serviceProvider,
        OpcUaClientRegistration registration)
    {
        return serviceProvider.GetRequiredKeyedService<IOpcUaSubjectClientSource>(registration.Key);
    }

    /// <summary>
    /// Gets the OPC UA server instance for the given registration.
    /// </summary>
    public static IOpcUaSubjectServer GetOpcUaSubjectServer(
        this IServiceProvider serviceProvider,
        OpcUaServerRegistration registration)
    {
        return serviceProvider.GetRequiredKeyedService<IOpcUaSubjectServer>(registration.Key);
    }
}
