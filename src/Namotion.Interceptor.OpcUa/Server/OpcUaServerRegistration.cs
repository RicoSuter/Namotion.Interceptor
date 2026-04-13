namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Represents a registered OPC UA server in the dependency injection container.
/// Use with <c>GetOpcUaSubjectServer</c> extension method on <see cref="IServiceProvider"/> to access the server instance.
/// </summary>
public sealed class OpcUaServerRegistration
{
    internal string Key { get; }

    internal OpcUaServerRegistration(string key)
    {
        Key = key;
    }
}
