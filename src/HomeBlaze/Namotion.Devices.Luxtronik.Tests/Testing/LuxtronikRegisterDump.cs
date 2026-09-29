using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using FluentModbus;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// Reads the raw registers of a Luxtronik 2.1 Smart Home Interface with read function codes only, independent of the
/// model so a mapping bug cannot hide a register, and writes them to JSON in the format <see cref="LuxtronikTestServer.LoadDump"/> reads.
/// </summary>
internal sealed class LuxtronikRegisterDump
{
    private const byte UnitId = 1;
    private const int FeatureFlagsAddress = 10000;
    private const int FeatureFlagCount = 12;
    private const int UnmappedInputAddress = 10001;

    // FluentModbus reports framing errors as this code; unlike a device rejection they leave the connection unusable.
    private const ModbusExceptionCode FramingErrorCode = (ModbusExceptionCode)255;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    // Spaces the requests out so the dump adds as little load as possible to a controller that is running a heat pump.
    private static readonly TimeSpan RequestPause = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcpClient;
    private ModbusTcpClient? _client;
    private bool _hasSentRequest;

    private LuxtronikRegisterDump(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public SortedDictionary<string, ushort> InputRegisters { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, ushort> HoldingRegisters { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, bool> DiscreteInputs { get; } = new(StringComparer.Ordinal);

    public List<string> Failures { get; } = [];

    public string UnmappedReadBehavior { get; private set; } = "not read";

    /// <summary>
    /// Reads every mapped block once, sequentially over one connection at a time, and writes the dump to
    /// <paramref name="dumpPath"/>, also when the read fails part way.
    /// </summary>
    public static async Task<LuxtronikRegisterDump> CaptureAsync(string host, int port, string dumpPath)
    {
        var dump = new LuxtronikRegisterDump(host, port);
        try
        {
            await dump.ReadAllAsync();
        }
        finally
        {
            dump.Disconnect();
            await File.WriteAllTextAsync(dumpPath, dump.ToJson());
        }

        return dump;
    }

    private async Task ReadAllAsync()
    {
        await ConnectAsync();

        foreach (var (start, end, _) in LuxtronikTestServer.InputRanges)
        {
            var data = await ReadBlockAsync($"input {start}",
                (client, token) => client.ReadInputRegistersAsync(UnitId, (ushort)start, (ushort)(end - start + 1), token));
            AddRegisters(data, start, InputRegisters);
        }

        foreach (var (start, end, _) in LuxtronikTestServer.HoldingRanges)
        {
            var data = await ReadBlockAsync($"holding {start}",
                (client, token) => client.ReadHoldingRegistersAsync(UnitId, (ushort)start, (ushort)(end - start + 1), token));
            AddRegisters(data, start, HoldingRegisters);
        }

        var bits = await ReadBlockAsync($"discrete {FeatureFlagsAddress}",
            (client, token) => client.ReadDiscreteInputsAsync(UnitId, FeatureFlagsAddress, FeatureFlagCount, token));
        if (bits is not null)
        {
            for (var index = 0; index < FeatureFlagCount; index++)
            {
                DiscreteInputs[FormatAddress(FeatureFlagsAddress + index)] = ((bits[index / 8] >> (index % 8)) & 1) != 0;
            }
        }

        // How the controller answers an unmapped read decides how the connector's split-on-failure behaves on it.
        var (value, failure) = await ReadAsync(
            (client, token) => client.ReadInputRegistersAsync(UnitId, UnmappedInputAddress, 1, token));
        UnmappedReadBehavior = value is not null
            ? $"answered with value {BinaryPrimitives.ReadUInt16BigEndian(value)}"
            : failure!;
    }

    private async Task<byte[]?> ReadBlockAsync(string description, Func<ModbusTcpClient, CancellationToken, Task<Memory<byte>>> read)
    {
        var (data, failure) = await ReadAsync(read);
        if (failure is not null)
        {
            Failures.Add($"{description}: {failure}");
        }

        return data;
    }

    private async Task<(byte[]? Data, string? Failure)> ReadAsync(Func<ModbusTcpClient, CancellationToken, Task<Memory<byte>>> read)
    {
        if (_hasSentRequest)
        {
            await Task.Delay(RequestPause);
        }

        _hasSentRequest = true;

        using var timeoutSource = new CancellationTokenSource(RequestTimeout);
        try
        {
            var data = await read(_client!, timeoutSource.Token);
            return (data.ToArray(), null);
        }
        catch (ModbusException exception) when (exception.ExceptionCode != FramingErrorCode)
        {
            return (null, $"exception response {exception.ExceptionCode}");
        }
        catch (Exception exception) when (exception is ModbusException or OperationCanceledException or TimeoutException or IOException)
        {
            var failure = timeoutSource.IsCancellationRequested || exception is TimeoutException
                ? "timeout"
                : $"error: {exception.Message}";

            await ConnectAsync();
            return (null, failure);
        }
    }

    // A timed-out request may still be answered later, and that late response would be taken as the
    // answer to the next request, so every timeout or connection error continues on a fresh connection.
    private async Task ConnectAsync()
    {
        Disconnect();

        var tcpClient = new TcpClient { NoDelay = true };
        try
        {
            await tcpClient.ConnectAsync(_host, _port).WaitAsync(RequestTimeout);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }

        var client = new ModbusTcpClient();
        client.Initialize(tcpClient, ModbusEndianness.BigEndian);
        _tcpClient = tcpClient;
        _client = client;
    }

    // A ModbusTcpClient initialized with an external TcpClient does not dispose it, so both are disposed.
    private void Disconnect()
    {
        _client?.Dispose();
        _tcpClient?.Dispose();
        _client = null;
        _tcpClient = null;
    }

    private string ToJson()
    {
        var dump = new
        {
            capturedAt = DateTimeOffset.UtcNow,
            unmappedReadBehavior = UnmappedReadBehavior,
            failures = Failures,
            inputRegisters = InputRegisters,
            holdingRegisters = HoldingRegisters,
            discreteInputs = DiscreteInputs
        };

        return JsonSerializer.Serialize(dump, JsonOptions);
    }

    private static void AddRegisters(byte[]? data, int start, SortedDictionary<string, ushort> target)
    {
        if (data is null)
        {
            return;
        }

        for (var index = 0; index < data.Length / 2; index++)
        {
            target[FormatAddress(start + index)] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index * 2));
        }
    }

    private static string FormatAddress(int address) => address.ToString(CultureInfo.InvariantCulture);
}
