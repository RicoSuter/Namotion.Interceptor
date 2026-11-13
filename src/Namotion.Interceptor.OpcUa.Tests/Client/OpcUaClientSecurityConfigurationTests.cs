using Namotion.Interceptor.OpcUa.Server;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaClientSecurityConfigurationTests
{
    [Fact]
    public void WhenTheServerConfigurationIsCreated_ThenItKeepsItsCertificateStore()
    {
        // Act
        var configuration = new OpcUaServerConfiguration();

        // Assert
        Assert.False(configuration.CleanCertificateStore);
    }
}
