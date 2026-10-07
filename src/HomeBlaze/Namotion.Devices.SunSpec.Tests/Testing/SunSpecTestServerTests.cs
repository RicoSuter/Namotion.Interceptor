using System.Net;
using FluentModbus;

namespace Namotion.Devices.SunSpec.Tests.Testing;

[Collection(SunSpecIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public class SunSpecTestServerTests
{
    [Fact]
    public void WhenReadingTheChain_ThenTheServerReturnsItAndRejectsReadsOutsideIt()
    {
        // Arrange
        var chain = new SunSpecTestChain().AddModel(1, new Dictionary<string, object?> { ["Mn"] = "Maker" });
        var expected = chain.Build();
        using var server = new SunSpecTestServer();
        server.Start((1, chain));
        using var client = new ModbusTcpClient();
        client.Connect(new IPEndPoint(IPAddress.Loopback, server.Port), ModbusEndianness.BigEndian);

        // Act
        var registers = client.ReadHoldingRegisters<ushort>(1, chain.MarkerAddress, expected.Length).ToArray();

        // Assert
        Assert.Equal(expected, registers);
        Assert.Throws<ModbusException>(() => client.ReadHoldingRegisters<ushort>(1, chain.MarkerAddress, expected.Length + 1));
    }

    [Fact]
    public void WhenRestartedWithoutAChainForAUnit_ThenReadsOfThatUnitAreRejected()
    {
        // Arrange
        var chain = new SunSpecTestChain().AddModel(1);
        using var server = new SunSpecTestServer(1, 2);
        server.Start((1, chain), (2, chain));
        server.Stop();

        // Act
        server.Start((1, chain));
        using var client = new ModbusTcpClient();
        client.Connect(new IPEndPoint(IPAddress.Loopback, server.Port), ModbusEndianness.BigEndian);

        // Assert
        var expected = chain.Build();
        Assert.Equal(expected, client.ReadHoldingRegisters<ushort>(1, chain.MarkerAddress, expected.Length).ToArray());
        Assert.Throws<ModbusException>(() => client.ReadHoldingRegisters<ushort>(2, chain.MarkerAddress, 2));
    }
}
