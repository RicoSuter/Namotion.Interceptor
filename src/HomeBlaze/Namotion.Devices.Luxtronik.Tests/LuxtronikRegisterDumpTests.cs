using System.Net;
using System.Net.Sockets;
using Namotion.Devices.Luxtronik.Tests.Testing;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
[Collection(LuxtronikIntegrationCollection.Name)]
public class LuxtronikRegisterDumpTests
{
    [Fact]
    public async Task WhenControllerRunsFirmware3921_ThenEveryMappedRegisterIsDumpedAndReplayable()
    {
        // Arrange
        var firmware = new Version(3, 92, 1);
        using var server = new LuxtronikTestServer(firmware);
        server.Start();
        server.SeedTypicalValues();
        var dumpPath = CreateDumpPath();
        var replayPath = CreateDumpPath();

        try
        {
            // Act
            var dump = await LuxtronikRegisterDump.CaptureAsync("127.0.0.1", server.Port, dumpPath, TimeSpan.Zero);

            using var replayServer = new LuxtronikTestServer(firmware);
            replayServer.Start();
            replayServer.LoadDump(await File.ReadAllTextAsync(dumpPath));
            var replay = await LuxtronikRegisterDump.CaptureAsync("127.0.0.1", replayServer.Port, replayPath, TimeSpan.Zero);

            // Assert
            Assert.Empty(dump.Failures);
            Assert.Equal("exception response IllegalDataAddress", dump.UnmappedReadBehavior);
            Assert.Equal(CountRegisters(LuxtronikTestServer.InputRanges), dump.InputRegisters.Count);
            Assert.Equal(CountRegisters(LuxtronikTestServer.HoldingRanges), dump.HoldingRegisters.Count);
            Assert.Equal(12, dump.DiscreteInputs.Count);
            Assert.All(dump.DiscreteInputs.Values, Assert.True);
            Assert.Equal(3, dump.InputRegisters["10400"]);
            Assert.Equal(92, dump.InputRegisters["10401"]);
            Assert.Equal(1, dump.InputRegisters["10402"]);
            Assert.Equal(352, dump.InputRegisters["10105"]);
            Assert.Equal(unchecked((ushort)-45), dump.InputRegisters["10108"]);
            Assert.Equal(350, dump.HoldingRegisters["10001"]);

            Assert.Equal(dump.InputRegisters, replay.InputRegisters);
            Assert.Equal(dump.HoldingRegisters, replay.HoldingRegisters);
            Assert.Equal(dump.DiscreteInputs, replay.DiscreteInputs);
        }
        finally
        {
            File.Delete(dumpPath);
            File.Delete(replayPath);
        }
    }

    [Fact]
    public async Task WhenControllerRunsFirmware390_ThenNewerRangesAreRecordedAsFailuresAndTheRestIsDumped()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 90, 1));
        server.Start();
        server.SeedTypicalValues();
        var dumpPath = CreateDumpPath();

        try
        {
            // Act
            var dump = await LuxtronikRegisterDump.CaptureAsync("127.0.0.1", server.Port, dumpPath, TimeSpan.Zero);

            // Assert
            Assert.Contains("input 10109: exception response IllegalDataAddress", dump.Failures);
            Assert.Contains("holding 10060: exception response IllegalDataAddress", dump.Failures);
            Assert.DoesNotContain("10109", dump.InputRegisters.Keys);
            Assert.Equal(90, dump.InputRegisters["10401"]);
            Assert.Equal(350, dump.HoldingRegisters["10001"]);
            Assert.Equal(12, dump.DiscreteInputs.Count);
            Assert.Contains("\"input 10109: exception response IllegalDataAddress\"", await File.ReadAllTextAsync(dumpPath));
        }
        finally
        {
            File.Delete(dumpPath);
        }
    }

    [Fact]
    public async Task WhenControllerDropsEveryConnection_ThenTheDumpStopsAfterThreeTransportFailures()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var acceptCancellation = new CancellationTokenSource();
        var acceptLoop = CloseEveryConnectionAsync(listener, acceptCancellation.Token);
        var dumpPath = CreateDumpPath();

        try
        {
            // Act
            var dump = await LuxtronikRegisterDump.CaptureAsync("127.0.0.1", port, dumpPath, TimeSpan.Zero);

            // Assert
            Assert.Equal(4, dump.Failures.Count);
            Assert.StartsWith("input 10000: ", dump.Failures[0]);
            Assert.StartsWith("input 10002: ", dump.Failures[1]);
            Assert.StartsWith("input 10006: ", dump.Failures[2]);
            Assert.Equal("aborted after 3 consecutive transport failures", dump.Failures[3]);
            Assert.Empty(dump.InputRegisters);
            Assert.Equal("not read", dump.UnmappedReadBehavior);
            Assert.Contains("aborted after 3 consecutive transport failures", await File.ReadAllTextAsync(dumpPath));
        }
        finally
        {
            await acceptCancellation.CancelAsync();
            await acceptLoop;
            File.Delete(dumpPath);
        }
    }

    private static async Task CloseEveryConnectionAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The test is over.
        }
    }

    private static string CreateDumpPath() => Path.Combine(Path.GetTempPath(), $"luxtronik-dump-{Guid.NewGuid():N}.json");

    private static int CountRegisters((int Start, int End, Version? MinimumFirmware)[] ranges) =>
        ranges.Sum(range => range.End - range.Start + 1);
}
