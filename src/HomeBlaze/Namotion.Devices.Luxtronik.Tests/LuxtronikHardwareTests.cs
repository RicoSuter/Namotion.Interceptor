using System.Globalization;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Xunit.Abstractions;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
public class LuxtronikHardwareTests
{
    private const int DefaultPort = 502;

    private readonly ITestOutputHelper _output;

    public LuxtronikHardwareTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LuxtronikHardwareFact]
    public async Task WhenReadingTheController_ThenRawRegistersAreDumped()
    {
        // Arrange
        var host = Environment.GetEnvironmentVariable("LUXTRONIK_HOST")!;
        var portText = Environment.GetEnvironmentVariable("LUXTRONIK_PORT");
        var port = string.IsNullOrWhiteSpace(portText) ? DefaultPort : int.Parse(portText, CultureInfo.InvariantCulture);
        var dumpPath = Environment.GetEnvironmentVariable("LUXTRONIK_DUMP_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "luxtronik-dump.json");
        _output.WriteLine($"Reading {host}:{port}, dump path {dumpPath}");

        // Act
        var dump = await LuxtronikRegisterDump.CaptureAsync(host, port, dumpPath);

        _output.WriteLine($"Dump written to {dumpPath}");
        _output.WriteLine($"Input registers: {dump.InputRegisters.Count}, holding registers: {dump.HoldingRegisters.Count}, discrete inputs: {dump.DiscreteInputs.Count}");
        _output.WriteLine($"Unmapped read: {dump.UnmappedReadBehavior}");
        _output.WriteLine($"Failures: {(dump.Failures.Count == 0 ? "none" : string.Join(", ", dump.Failures))}");

        // Assert
        Assert.True(dump.InputRegisters.ContainsKey("10400"), "The firmware registers must be readable.");
        _output.WriteLine($"Firmware {dump.InputRegisters["10400"]}.{dump.InputRegisters["10401"]}.{dump.InputRegisters["10402"]}");
    }
}
