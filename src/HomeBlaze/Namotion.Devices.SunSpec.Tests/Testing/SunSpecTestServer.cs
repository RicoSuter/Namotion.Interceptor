using System.Net;
using System.Net.Sockets;
using FluentModbus;

namespace Namotion.Devices.SunSpec.Tests.Testing;

/// <summary>
/// In-process Modbus TCP server serving a SunSpec chain per unit ID. Reads outside a unit's chain are rejected with
/// "illegal data address", like on a real device.
/// </summary>
internal sealed class SunSpecTestServer : IDisposable
{
    private readonly byte[] _unitIds;
    private readonly Lock _rangesLock = new();
    private readonly Dictionary<byte, (int Start, int End)> _ranges = [];
    private readonly Dictionary<byte, ModbusExceptionCode> _unitExceptions = [];
    private ModbusTcpServer? _server;

    public SunSpecTestServer(params byte[] unitIds)
    {
        // Single-unit mode answers only unit 0, so the server always runs with explicit units.
        _unitIds = unitIds.Length == 0 ? [1] : unitIds;
        Port = GetFreeTcpPort();
    }

    public int Port { get; }

    public int ConnectionCount => _server?.ConnectionCount ?? 0;

    /// <summary>
    /// Writes the chains, then starts listening, so a client never sees a unit without its chain.
    /// </summary>
    public void Start(params (byte UnitId, SunSpecTestChain Chain)[] chains)
    {
        var server = new ModbusTcpServer(true);
        foreach (var unitId in _unitIds)
        {
            server.AddUnit(unitId);
        }

        server.RequestValidator = ValidateRequest;
        lock (_rangesLock)
        {
            // The ranges of a previous run would make the fresh server accept reads of units without a chain.
            _ranges.Clear();
        }

        _server = server;
        foreach (var (unitId, chain) in chains)
        {
            WriteChain(chain, unitId);
        }

        server.Start(new IPEndPoint(IPAddress.Loopback, Port));
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

    /// <summary>
    /// Writes a chain to a unit, replacing its previous chain.
    /// </summary>
    public void WriteChain(SunSpecTestChain chain, byte unitId = 1)
    {
        var registers = chain.Build();
        var server = GetServer();
        lock (server.Lock)
        {
            var holdingRegisters = server.GetHoldingRegisters(unitId);
            lock (_rangesLock)
            {
                if (_ranges.TryGetValue(unitId, out var previous))
                {
                    holdingRegisters[previous.Start..previous.End].Clear();
                }

                _ranges[unitId] = (chain.MarkerAddress, chain.MarkerAddress + registers.Length);
            }

            for (var index = 0; index < registers.Length; index++)
            {
                holdingRegisters.SetBigEndian(chain.MarkerAddress + index, registers[index]);
            }
        }
    }

    /// <summary>
    /// Writes consecutive registers in one step, so a poll sees all or none of them.
    /// </summary>
    public void WriteRegisters(int address, IReadOnlyList<ushort> values, byte unitId = 1)
    {
        var server = GetServer();
        lock (server.Lock)
        {
            var holdingRegisters = server.GetHoldingRegisters(unitId);
            for (var index = 0; index < values.Count; index++)
            {
                holdingRegisters.SetBigEndian(address + index, values[index]);
            }
        }
    }

    /// <summary>
    /// Answers every request to a unit with <paramref name="exceptionCode"/>, such as a gateway error.
    /// </summary>
    public void SetUnitException(byte unitId, ModbusExceptionCode exceptionCode)
    {
        lock (_rangesLock)
        {
            _unitExceptions[unitId] = exceptionCode;
        }
    }

    public void Dispose() => Stop();

    private ModbusTcpServer GetServer() => _server ?? throw new InvalidOperationException("The test server is not started.");

    private ModbusExceptionCode ValidateRequest(byte unitId, ModbusFunctionCode functionCode, ushort address, ushort quantity)
    {
        if (functionCode != ModbusFunctionCode.ReadHoldingRegisters)
        {
            return ModbusExceptionCode.IllegalFunction;
        }

        lock (_rangesLock)
        {
            if (_unitExceptions.TryGetValue(unitId, out var exceptionCode))
            {
                return exceptionCode;
            }

            return _ranges.TryGetValue(unitId, out var range) && address >= range.Start && address + quantity <= range.End
                ? ModbusExceptionCode.OK
                : ModbusExceptionCode.IllegalDataAddress;
        }
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
