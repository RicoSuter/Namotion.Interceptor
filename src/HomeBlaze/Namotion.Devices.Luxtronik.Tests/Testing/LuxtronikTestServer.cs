using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentModbus;
using Namotion.Devices.Luxtronik.Enums;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// In-process Modbus server imitating a Luxtronik 2.1 Smart Home Interface on unit 1. A read that touches an unmapped
/// address, or a 3.92 address on older firmware, is rejected with "illegal data address" like the real controller.
/// </summary>
internal sealed class LuxtronikTestServer : IDisposable
{
    private const byte UnitId = 1;
    private const int FeatureFlagsAddress = 10000;
    private const int FeatureFlagCount = 12;

    // Mapped registers per the AIT manual and python-luxtronik; Requires392 marks registers added in firmware 3.92.
    private static readonly (int Start, int End, bool Requires392)[] InputRanges =
    [
        (10000, 10000, false), (10002, 10004, false), (10006, 10007, false),
        (10100, 10108, false), (10109, 10113, true), (10120, 10124, false),
        (10140, 10143, false), (10150, 10153, false), (10160, 10163, false),
        (10201, 10207, false), (10300, 10302, false), (10310, 10319, false), (10320, 10329, true),
        (10350, 10356, true), (10360, 10361, true), (10400, 10402, false),
        (10404, 10413, true), (10416, 10417, true), (10500, 10502, true)
    ];

    private static readonly (int Start, int End, bool Requires392)[] HoldingRanges =
    [
        (10000, 10002, false), (10003, 10003, true), (10005, 10007, false), (10008, 10008, true),
        (10010, 10012, false), (10013, 10013, true), (10015, 10017, false),
        (10020, 10022, false), (10023, 10023, true), (10025, 10027, false),
        (10030, 10032, false), (10033, 10033, true), (10035, 10037, false),
        (10040, 10041, false), (10050, 10051, true), (10052, 10053, false),
        (10060, 10060, true), (10065, 10067, true), (10070, 10071, true)
    ];

    private static readonly Version Firmware392 = new(3, 92, 0);

    private readonly Version _firmware;
    private readonly bool _supportsDiscreteInputs;
    private ModbusTcpServer? _server;

    public LuxtronikTestServer(Version firmware, bool supportsDiscreteInputs = true)
    {
        _firmware = firmware;
        _supportsDiscreteInputs = supportsDiscreteInputs;
        Port = GetFreeTcpPort();
    }

    public int Port { get; }

    /// <summary>
    /// Starts the server with the firmware version set and every function configured.
    /// </summary>
    public void Start()
    {
        var server = new ModbusTcpServer(true);
        server.AddUnit(UnitId);
        server.RequestValidator = ValidateRequest;
        server.Start(new IPEndPoint(IPAddress.Loopback, Port));
        _server = server;

        SetInput(10400, (ushort)_firmware.Major);
        SetInput(10401, (ushort)_firmware.Minor);
        SetInput(10402, (ushort)Math.Max(_firmware.Build, 0));
        SetFeatures(Enum.GetValues<LuxtronikFeature>().Where(feature => feature != LuxtronikFeature.None).ToArray());
    }

    /// <summary>
    /// Stops the server; the connected clients lose their connection. Safe to call more than once.
    /// </summary>
    public void Stop()
    {
        var server = _server;
        _server = null;
        if (server is not null)
        {
            server.Stop();
            server.Dispose();
        }
    }

    public void SeedTypicalValues()
    {
        SetInput<ushort>(10000, 1);        // compressor 1 running
        SetInput<ushort>(10002, 0);        // operation mode: heating
        SetInput<ushort>(10003, 3);        // heating: running
        SetInput<short>(10105, 352);       // flow 35.2 degrees
        SetInput<short>(10108, -45);       // outside -4.5 degrees
        SetInput<short>(10110, 81);        // heat source inlet 8.1 degrees
        SetInput<short>(10120, 482);       // hot water 48.2 degrees
        SetInput<short>(10300, 65);        // heating power 6.5 kW
        SetInput<ushort>(10301, 15);       // electrical power 1.5 kW
        SetInput<int>(10310, 123456);      // electrical energy 12345.6 kWh
        SetInput<int>(10320, 456789);      // thermal energy 45678.9 kWh
        SetInput<uint>(10404, 12345);      // heat pump runtime hours
        SetHolding<ushort>(10001, 350);    // heating setpoint 35.0 degrees
        SetHolding<ushort>(10011, 280);    // mixing circuit 1 heating setpoint 28.0 degrees
        SetHolding<ushort>(10041, 300);    // power limit 30.0 kW
    }

    public void SetInput<T>(int address, T value) where T : unmanaged
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetInputRegisters(UnitId).SetBigEndian(address, value);
        }
    }

    public void SetHolding<T>(int address, T value) where T : unmanaged
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetHoldingRegisters(UnitId).SetBigEndian(address, value);
        }
    }

    public void SetFeatures(params LuxtronikFeature[] configuredFeatures)
    {
        var server = GetServer();
        lock (server.Lock)
        {
            var discreteInputs = server.GetDiscreteInputs(UnitId);
            for (var index = 0; index < FeatureFlagCount; index++)
            {
                discreteInputs.Set(FeatureFlagsAddress + index, configuredFeatures.Contains((LuxtronikFeature)index));
            }
        }
    }

    /// <summary>
    /// Loads a raw register dump in the format written by the hardware test.
    /// </summary>
    public void LoadDump(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var register in root.GetProperty("inputRegisters").EnumerateObject())
        {
            SetInput(int.Parse(register.Name, CultureInfo.InvariantCulture), register.Value.GetUInt16());
        }

        foreach (var register in root.GetProperty("holdingRegisters").EnumerateObject())
        {
            SetHolding(int.Parse(register.Name, CultureInfo.InvariantCulture), register.Value.GetUInt16());
        }

        if (root.TryGetProperty("discreteInputs", out var discreteInputs))
        {
            var server = GetServer();
            lock (server.Lock)
            {
                foreach (var input in discreteInputs.EnumerateObject())
                {
                    server.GetDiscreteInputs(UnitId).Set(int.Parse(input.Name, CultureInfo.InvariantCulture), input.Value.GetBoolean());
                }
            }
        }
    }

    public void Dispose() => Stop();

    private ModbusTcpServer GetServer() => _server ?? throw new InvalidOperationException("The test server is not started.");

    private ModbusExceptionCode ValidateRequest(byte unitId, ModbusFunctionCode functionCode, ushort address, ushort quantity)
    {
        var isMapped = functionCode switch
        {
            ModbusFunctionCode.ReadInputRegisters => IsMapped(InputRanges, address, quantity),
            ModbusFunctionCode.ReadHoldingRegisters => IsMapped(HoldingRanges, address, quantity),
            ModbusFunctionCode.ReadDiscreteInputs => _supportsDiscreteInputs &&
                address >= FeatureFlagsAddress && address + quantity <= FeatureFlagsAddress + FeatureFlagCount,
            _ => false
        };

        return isMapped ? ModbusExceptionCode.OK : ModbusExceptionCode.IllegalDataAddress;
    }

    private bool IsMapped((int Start, int End, bool Requires392)[] ranges, int address, int quantity)
    {
        var supports392 = _firmware >= Firmware392;
        for (var current = address; current < address + quantity; current++)
        {
            var isKnown = false;
            foreach (var range in ranges)
            {
                if (current >= range.Start && current <= range.End && (supports392 || !range.Requires392))
                {
                    isKnown = true;
                    break;
                }
            }

            if (!isKnown)
            {
                return false;
            }
        }

        return true;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
