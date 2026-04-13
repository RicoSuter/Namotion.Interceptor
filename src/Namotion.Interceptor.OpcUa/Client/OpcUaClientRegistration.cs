namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Represents a registered OPC UA client source in the dependency injection container.
/// Use with <c>GetOpcUaSubjectClientSource</c> extension method on <see cref="IServiceProvider"/> to access the client instance.
/// </summary>
public sealed class OpcUaClientRegistration
{
    internal string Key { get; }

    internal OpcUaClientRegistration(string key)
    {
        Key = key;
    }
}
