using System.Net.Sockets;
using FluentModbus;

namespace Namotion.Interceptor.Modbus.Client.Transport;

/// <summary>
/// One Modbus TCP connection serving one read at a time: a <see cref="ReadAsync"/> started while another is in flight
/// throws <see cref="InvalidOperationException"/>. <see cref="Dispose"/> may be called concurrently to abort an
/// in-flight read.
/// </summary>
internal sealed class ModbusConnection : IModbusRegisterReader, IDisposable
{
    // FluentModbus throws ModbusException with this code for framing errors (invalid protocol identifier,
    // function code or length). Those are not device rejections and must propagate as a lost connection.
    private const ModbusExceptionCode FramingErrorCode = (ModbusExceptionCode)255;

    private readonly TcpClient _tcpClient;
    private readonly ModbusTcpClient _client;
    private readonly TimeSpan _requestTimeout;

    // Reused by every request of this connection and replaced only after it fired.
    private CancellationTokenSource _timeoutSource = new();
    private int _disposed;
    private int _isReading;

    private ModbusConnection(TcpClient tcpClient, ModbusTcpClient client, TimeSpan requestTimeout)
    {
        _tcpClient = tcpClient;
        _client = client;
        _requestTimeout = requestTimeout;
    }

    public static async Task<ModbusConnection> ConnectAsync(
        string host, int port, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        var tcpClient = new TcpClient { NoDelay = true };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(requestTimeout);
        try
        {
            await tcpClient.ConnectAsync(host, port, timeoutSource.Token).ConfigureAwait(false);

            var client = new ModbusTcpClient();
            client.Initialize(tcpClient, ModbusEndianness.BigEndian);
            return new ModbusConnection(tcpClient, client, requestTimeout);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            tcpClient.Dispose();
            throw new TimeoutException($"Connecting to {host}:{port} did not complete within {requestTimeout}.", exception);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(
        byte unitId, ModbusAddressSpace space, int address, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        // FluentModbus shares one frame buffer and transaction ID per client, so overlapping reads would mix up responses.
        if (Interlocked.CompareExchange(ref _isReading, 1, 0) != 0)
        {
            throw new InvalidOperationException("A read is already in progress on this Modbus connection.");
        }

        var timeoutSource = _timeoutSource;
        var registration = default(CancellationTokenRegistration);
        try
        {
            timeoutSource.CancelAfter(_requestTimeout);
            registration = cancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(), timeoutSource);

            var token = timeoutSource.Token;
            return space switch
            {
                ModbusAddressSpace.HoldingRegister => await _client.ReadHoldingRegistersAsync(unitId, (ushort)address, (ushort)count, token).ConfigureAwait(false),
                ModbusAddressSpace.InputRegister => await _client.ReadInputRegistersAsync(unitId, (ushort)address, (ushort)count, token).ConfigureAwait(false),
                ModbusAddressSpace.Coil => await _client.ReadCoilsAsync(unitId, address, count, token).ConfigureAwait(false),
                ModbusAddressSpace.DiscreteInput => await _client.ReadDiscreteInputsAsync(unitId, address, count, token).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(space), space, null)
            };
        }
        catch (ModbusException exception) when (exception.ExceptionCode is not ModbusExceptionCode.OK and not FramingErrorCode)
        {
            throw new ModbusResponseException((int)exception.ExceptionCode, exception.Message, exception);
        }
        catch (Exception exception) when (timeoutSource.IsCancellationRequested)
        {
            // FluentModbus closes the stream when its token fires, so both cases leave the connection unusable.
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The Modbus request did not complete within {_requestTimeout}.", exception);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            if (!timeoutSource.TryReset())
            {
                _timeoutSource = new CancellationTokenSource();
                timeoutSource.Dispose();
            }

            // Released last so the next read sees the replaced timeout source.
            Volatile.Write(ref _isReading, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Closing the socket aborts an in-flight read. The Modbus client is deliberately not disposed: that returns its
        // frame buffer to the shared ArrayPool while the read, or the caller copying its result, may still use it. The
        // buffer is collected with the client instead. The timeout source is left to the in-flight read, which still
        // resets or replaces it; it never creates a wait handle, so it holds nothing that needs disposing.
        _tcpClient.Dispose();
    }
}
