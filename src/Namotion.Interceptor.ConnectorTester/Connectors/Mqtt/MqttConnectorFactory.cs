using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.ConnectorTester.Connectors.Mqtt;

public sealed class MqttConnectorFactory : IConnectorFactory
{
    public ConnectorKind Kind => ConnectorKind.Mqtt;
    public int DefaultPort => 1883;

    public void RegisterServer(IServiceCollection services, TestNode root, int port)
    {
        services.AddSingleton(root);
        services.AddMqttSubjectServer(
            _ => root,
            _ => new MqttServerConfiguration
            {
                BrokerPort = port,
                Mapper = new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
                DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
                UseRetainedMessages = true,
                SourceTimestampSerializer = MqttTickTimestampSerialization.Serialize,
                SourceTimestampDeserializer = MqttTickTimestampSerialization.Deserialize
            });
    }

    public void RegisterClient(IServiceCollection services, TestNode root, ParticipantConfiguration participantConfiguration, int port)
    {
        services.AddMqttSubjectClientSource(
            _ => root,
            _ => new MqttClientConfiguration
            {
                BrokerHost = "localhost",
                BrokerPort = port,
                Mapper = new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
                DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
                UseRetainedMessages = true,
                SourceTimestampSerializer = MqttTickTimestampSerialization.Serialize,
                SourceTimestampDeserializer = MqttTickTimestampSerialization.Deserialize,
                ReconnectDelay = TimeSpan.FromSeconds(1),
                MaximumReconnectDelay = TimeSpan.FromSeconds(10),
                HealthCheckInterval = TimeSpan.FromSeconds(5),
                CircuitBreakerFailureThreshold = 3,
                CircuitBreakerCooldown = TimeSpan.FromSeconds(10),
                WriteRetryQueueSize = 10_000,
            });
    }
}
