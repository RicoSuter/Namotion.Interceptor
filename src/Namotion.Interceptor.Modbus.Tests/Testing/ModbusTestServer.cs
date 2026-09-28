using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentModbus;

namespace Namotion.Interceptor.Modbus.Tests.Testing;

/// <summary>
/// In-process Modbus TCP server on a free loopback port. Register values are written in wire (big endian) order.
/// </summary>
internal sealed class ModbusTestServer : IDisposable
{
    private readonly byte[] _unitIds;
    private readonly Lock _rejectionsLock = new();
    private readonly List<(byte UnitId, ModbusAddressSpace Space, int Address, ModbusExceptionCode ExceptionCode)> _rejectedAddresses = [];
    private readonly ConcurrentQueue<(byte UnitId, ModbusFunctionCode FunctionCode, int Address, int Quantity)> _requests = new();
    private ModbusTcpServer? _server;

    public ModbusTestServer(params byte[] unitIds)
    {
        // Single-unit mode answers only unit 0, so the server always runs with explicit units.
        _unitIds = unitIds.Length == 0 ? [1] : unitIds;
        Port = GetFreeTcpPort();
    }

    public int Port { get; }

    public int ConnectionCount => _server?.ConnectionCount ?? 0;

    public IReadOnlyList<(byte UnitId, ModbusFunctionCode FunctionCode, int Address, int Quantity)> Requests => _requests.ToArray();

    public void Start()
    {
        var server = new ModbusTcpServer(true);
        foreach (var unitId in _unitIds)
        {
            server.AddUnit(unitId);
        }

        server.RequestValidator = ValidateRequest;
        server.Start(new IPEndPoint(IPAddress.Loopback, Port));
        _server = server;
    }

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

    public void SetHoldingRegister<T>(int address, T value, byte unitId = 1) where T : unmanaged
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetHoldingRegisters(unitId).SetBigEndian(address, value);
        }
    }

    public void SetInputRegister<T>(int address, T value, byte unitId = 1) where T : unmanaged
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetInputRegisters(unitId).SetBigEndian(address, value);
        }
    }

    public void SetCoil(int address, bool value, byte unitId = 1)
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetCoils(unitId).Set(address, value);
        }
    }

    public void SetDiscreteInput(int address, bool value, byte unitId = 1)
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetDiscreteInputs(unitId).Set(address, value);
        }
    }

    public void RejectAddress(
        ModbusAddressSpace space, int address, byte unitId = 1, ModbusExceptionCode exceptionCode = ModbusExceptionCode.IllegalDataAddress)
    {
        lock (_rejectionsLock)
        {
            _rejectedAddresses.Add((unitId, space, address, exceptionCode));
        }
    }

    public void Dispose() => Stop();

    private ModbusTcpServer GetServer() => _server ?? throw new InvalidOperationException("The test server is not started.");

    private ModbusExceptionCode ValidateRequest(byte unitId, ModbusFunctionCode functionCode, ushort address, ushort quantity)
    {
        _requests.Enqueue((unitId, functionCode, address, quantity));

        ModbusAddressSpace? space = functionCode switch
        {
            ModbusFunctionCode.ReadHoldingRegisters or ModbusFunctionCode.WriteSingleRegister or ModbusFunctionCode.WriteMultipleRegisters
                => ModbusAddressSpace.HoldingRegister,
            ModbusFunctionCode.ReadInputRegisters => ModbusAddressSpace.InputRegister,
            ModbusFunctionCode.ReadCoils or ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteMultipleCoils
                => ModbusAddressSpace.Coil,
            ModbusFunctionCode.ReadDiscreteInputs => ModbusAddressSpace.DiscreteInput,
            _ => null
        };

        lock (_rejectionsLock)
        {
            foreach (var rejected in _rejectedAddresses)
            {
                if (rejected.UnitId == unitId && rejected.Space == space &&
                    rejected.Address >= address && rejected.Address < address + quantity)
                {
                    return rejected.ExceptionCode;
                }
            }
        }

        return ModbusExceptionCode.OK;
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
