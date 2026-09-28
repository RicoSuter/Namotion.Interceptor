using System.Net;
using System.Net.Sockets;
using Namotion.Interceptor.Modbus.Tests.Testing;
using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus.Tests.Transport;

[Trait("Category", "Integration")]
[Collection(ModbusIntegrationCollection.Name)]
public class ModbusConnectionTests
{
    private static Task<ModbusConnection> ConnectAsync(ModbusTestServer server, TimeSpan? requestTimeout = null)
        => ModbusConnection.ConnectAsync("127.0.0.1", server.Port, requestTimeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);

    [Fact]
    public async Task WhenReadingEachSpace_ThenWireBytesAreReturned()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        server.SetHoldingRegister<short>(10, -2);
        server.SetInputRegister<ushort>(10, 0xABCD);
        server.SetCoil(3, true);
        server.SetDiscreteInput(1, true);
        using var connection = await ConnectAsync(server);

        // Act
        var holding = (await connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 10, 1, CancellationToken.None)).ToArray();
        var input = (await connection.ReadAsync(1, ModbusAddressSpace.InputRegister, 10, 1, CancellationToken.None)).ToArray();
        var coils = (await connection.ReadAsync(1, ModbusAddressSpace.Coil, 0, 8, CancellationToken.None)).ToArray();
        var discreteInputs = (await connection.ReadAsync(1, ModbusAddressSpace.DiscreteInput, 0, 4, CancellationToken.None)).ToArray();

        // Assert
        Assert.Equal(new byte[] { 0xFF, 0xFE }, holding);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, input);
        Assert.Equal(new byte[] { 0x08 }, coils);
        Assert.Equal(new byte[] { 0x02 }, discreteInputs);
    }

    [Fact]
    public async Task WhenDeviceRejectsAddress_ThenResponseExceptionIsThrownAndConnectionStaysUsable()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        server.RejectAddress(ModbusAddressSpace.HoldingRegister, 5);
        server.SetHoldingRegister<short>(0, 7);
        using var connection = await ConnectAsync(server);

        // Act
        var exception = await Assert.ThrowsAsync<ModbusResponseException>(() =>
            connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 4, 2, CancellationToken.None));
        var afterwards = (await connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None)).ToArray();

        // Assert
        Assert.Equal(2, exception.ExceptionCode);
        Assert.Equal(new byte[] { 0x00, 0x07 }, afterwards);
    }

    [Fact]
    public async Task WhenUnitDoesNotAnswer_ThenTimeoutExceptionIsThrown()
    {
        // Arrange
        using var server = new ModbusTestServer(1);
        server.Start();
        using var connection = await ConnectAsync(server, TimeSpan.FromMilliseconds(300));

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() =>
            connection.ReadAsync(9, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None));
    }

    [Fact]
    public async Task WhenCallerCancels_ThenOperationCanceledExceptionIsThrown()
    {
        // Arrange
        using var server = new ModbusTestServer(1);
        server.Start();
        using var connection = await ConnectAsync(server, TimeSpan.FromSeconds(30));
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connection.ReadAsync(9, ModbusAddressSpace.HoldingRegister, 0, 1, cancellationTokenSource.Token));
    }

    [Fact]
    public async Task WhenResponseHasInvalidProtocolIdentifier_ThenItIsNotReportedAsDeviceRejection()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource();
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var request = new byte[12];
            await stream.ReadExactlyAsync(request);

            // Echoed transaction ID, protocol ID 1 instead of 0, length 5, unit, function 3, 2 bytes, value 7.
            await stream.WriteAsync(new byte[] { request[0], request[1], 0x00, 0x01, 0x00, 0x05, request[6], 0x03, 0x02, 0x00, 0x07 });
            await release.Task;
        });
        using var connection = await ModbusConnection.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), CancellationToken.None);

        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() =>
                connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None));

            // Assert
            Assert.NotNull(exception);
            Assert.IsNotType<ModbusResponseException>(exception);
            Assert.IsNotType<TimeoutException>(exception);
        }
        finally
        {
            release.TrySetResult();
            await serverTask;
        }
    }

    [Fact]
    public async Task WhenServerStops_ThenReadFailsWithConnectionError()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        using var connection = await ConnectAsync(server, TimeSpan.FromSeconds(2));
        server.Stop();

        // Act
        var exception = await Record.ExceptionAsync(() =>
            connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None));

        // Assert
        Assert.NotNull(exception);
        Assert.IsNotType<ModbusResponseException>(exception);
    }

    [Fact]
    public async Task WhenDisposed_ThenReadThrowsObjectDisposedException()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        var connection = await ConnectAsync(server);
        connection.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None));
    }
}
