using Opc.Ua;

namespace Namotion.Interceptor.ConnectorTester.Configuration;

/// <summary>Settings used only when <see cref="ConnectorTesterConfiguration.Connector"/> is "opcua".</summary>
public class OpcUaConfiguration
{
    /// <summary>
    /// Minimum security mode every client requires of the server endpoint. Any mode other than
    /// <see cref="MessageSecurityMode.None"/> makes the clients accept the server certificate, gives each participant
    /// its own certificate store and shortens the security token lifetime so secure channels renew within a cycle.
    /// </summary>
    public MessageSecurityMode MinimumSecurityMode { get; set; } = MessageSecurityMode.None;

    /// <summary>Whether <see cref="MinimumSecurityMode"/> requires a secured endpoint.</summary>
    public bool IsSecured => MinimumSecurityMode != MessageSecurityMode.None;
}
