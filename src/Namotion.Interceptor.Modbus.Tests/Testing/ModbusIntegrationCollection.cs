namespace Namotion.Interceptor.Modbus.Tests.Testing;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModbusIntegrationCollection
{
    private ModbusIntegrationCollection()
    {
    }

    public const string Name = "Modbus integration";
}
