using System.Net;
using System.Net.Sockets;
using FluentModbus;
using Namotion.Devices.Luxtronik.Enums;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// In-process Modbus server imitating a Luxtronik 2.1 Smart Home Interface on unit 1. A read that touches an unmapped
/// address, or an address newer than the firmware, is rejected with "illegal data address" like the real controller.
/// </summary>
internal sealed class LuxtronikTestServer : IDisposable
{
    private const byte UnitId = 1;
    private const int FeatureFlagsAddress = 10000;
    private const int FeatureFlagCount = 12;

    private static readonly Version Firmware392 = new(3, 92, 0);
    private static readonly Version Firmware3921 = new(3, 92, 1);

    // Mapped registers per the AIT manual and python-luxtronik; MinimumFirmware is null for registers of every firmware.
    private static readonly (int Start, int End, Version? MinimumFirmware)[] InputRanges =
    [
        (10000, 10000, null), (10002, 10004, null), (10006, 10007, null),
        (10100, 10108, null), (10109, 10113, Firmware392), (10120, 10124, null),
        (10140, 10143, null), (10150, 10153, null), (10160, 10163, null),
        (10201, 10204, null), (10207, 10207, null),
        (10300, 10302, null), (10310, 10319, null), (10320, 10329, Firmware392),
        (10350, 10356, Firmware392), (10360, 10361, Firmware392), (10400, 10402, null),
        (10404, 10413, Firmware392), (10416, 10417, Firmware392), (10500, 10502, Firmware392)
    ];

    private static readonly (int Start, int End, Version? MinimumFirmware)[] HoldingRanges =
    [
        (10000, 10002, null), (10003, 10003, Firmware392), (10005, 10007, null), (10008, 10008, Firmware392),
        (10010, 10012, null), (10013, 10013, Firmware392), (10015, 10017, null),
        (10020, 10022, null), (10023, 10023, Firmware392), (10025, 10027, null),
        (10030, 10032, null), (10033, 10033, Firmware392), (10035, 10037, null),
        (10040, 10041, null), (10050, 10051, Firmware392), (10052, 10053, null),
        (10060, 10060, Firmware3921), (10065, 10067, Firmware392), (10070, 10071, Firmware392)
    ];

    private readonly Version _firmware;
    private readonly bool _supportsDiscreteInputs;
    private readonly Lock _rejectionLock = new();
    private int _pendingFeatureReadRejections;
    private ModbusExceptionCode _featureReadRejectionCode;
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
    /// Rejects the next <paramref name="count"/> reads of the feature flags with <paramref name="exceptionCode"/>.
    /// </summary>
    public void RejectFeatureReads(int count, ModbusExceptionCode exceptionCode)
    {
        lock (_rejectionLock)
        {
            _pendingFeatureReadRejections = count;
            _featureReadRejectionCode = exceptionCode;
        }
    }

    public void Dispose() => Stop();

    private ModbusTcpServer GetServer() => _server ?? throw new InvalidOperationException("The test server is not started.");

    private ModbusExceptionCode ValidateRequest(byte unitId, ModbusFunctionCode functionCode, ushort address, ushort quantity)
    {
        if (functionCode == ModbusFunctionCode.ReadDiscreteInputs)
        {
            lock (_rejectionLock)
            {
                if (_pendingFeatureReadRejections > 0)
                {
                    _pendingFeatureReadRejections--;
                    return _featureReadRejectionCode;
                }
            }
        }

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

    private bool IsMapped((int Start, int End, Version? MinimumFirmware)[] ranges, int address, int quantity)
    {
        for (var current = address; current < address + quantity; current++)
        {
            var isKnown = false;
            foreach (var range in ranges)
            {
                if (current >= range.Start && current <= range.End &&
                    (range.MinimumFirmware is null || _firmware >= range.MinimumFirmware))
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
