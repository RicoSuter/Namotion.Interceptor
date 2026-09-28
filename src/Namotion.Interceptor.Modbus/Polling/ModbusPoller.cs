using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus.Polling;

/// <summary>
/// Runs the read cycle of one connection. Not thread-safe except <see cref="RequestReapply"/>: the source calls
/// <see cref="ReadAsync"/> and <see cref="ApplyChanges{TState}"/> strictly one after another.
/// </summary>
internal sealed class ModbusPoller
{
    private readonly ModbusRegisterBinding[] _bindings;
    private readonly Dictionary<PropertyReference, ModbusRegisterBinding> _bindingsByProperty;
    private readonly int _maximumRegisterGap;
    private readonly ModbusPollingMetrics _metrics;
    private readonly ILogger _logger;

    // Keyed by request rather than batch instance so a replan does not log a still failing request again.
    private readonly HashSet<(byte UnitId, ModbusAddressSpace Space, int StartAddress, int Count)> _failingBatches = [];
    private ModbusReadBatch[] _batches;

    public ModbusPoller(
        IReadOnlyCollection<ModbusRegisterBinding> bindings, int maximumRegisterGap,
        ModbusPollingMetrics metrics, ILogger logger)
    {
        _bindings = bindings.ToArray();
        _bindingsByProperty = _bindings.ToDictionary(binding => binding.Property, PropertyReference.Comparer);
        _maximumRegisterGap = maximumRegisterGap;
        _metrics = metrics;
        _logger = logger;
        _batches = ModbusReadPlanner.Plan(_bindings, maximumRegisterGap);
        _metrics.SetPlan(_batches.Length, unavailableProperties: 0);
    }

    public IReadOnlyList<ModbusRegisterBinding> Bindings => _bindings;

    public IReadOnlyList<ModbusReadBatch> Batches => _batches;

    /// <summary>
    /// Gets the path of a mapped property, or its name when this poller does not map it.
    /// </summary>
    public string GetPath(PropertyReference property)
        => _bindingsByProperty.TryGetValue(property, out var binding) ? binding.Path : property.Name;

    public void RequestReapply(PropertyReference property)
    {
        if (_bindingsByProperty.TryGetValue(property, out var binding))
        {
            binding.RequestReapply();
        }
    }

    /// <summary>
    /// Reads every batch into the bindings' current raw buffers. Modbus exception responses are handled here;
    /// any other exception means the connection is lost and propagates.
    /// </summary>
    public async Task ReadAsync(IModbusRegisterReader reader, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        foreach (var binding in _bindings)
        {
            binding.HasCurrent = false;
        }

        var isReplanRequired = false;
        foreach (var batch in _batches)
        {
            try
            {
                var data = await reader.ReadAsync(batch.UnitId, batch.Space, batch.StartAddress, batch.Count, cancellationToken).ConfigureAwait(false);

                // The reader may hand out a pooled buffer that its next read overwrites, so copy before reading on.
                foreach (var binding in batch.Bindings)
                {
                    CopyToBinding(binding, data.Span, batch.StartAddress);
                }

                if (_failingBatches.Count > 0 && _failingBatches.Remove(GetKey(batch)))
                {
                    _logger.LogInformation(
                        "Modbus read of {Space} {Address} (unit {UnitId}) succeeds again.",
                        batch.Space, batch.StartAddress, batch.UnitId);
                }
            }
            catch (ModbusResponseException exception) when (exception.IsPermanentRejection && batch.Bindings.Length > 1)
            {
                _metrics.RecordFailedBatch();
                _failingBatches.Remove(GetKey(batch));
                await ReadIndividuallyAsync(reader, batch, exception, cancellationToken).ConfigureAwait(false);
                isReplanRequired = true;
            }
            catch (ModbusResponseException exception)
            {
                // Skipped for this cycle only: its values keep their last value and the plan stays as it is.
                _metrics.RecordFailedBatch();
                LogFailedRequestOnce(GetKey(batch), batch.Bindings[0].Path, exception);
            }
        }

        if (isReplanRequired)
        {
            var availableBindings = _bindings.Where(binding => !binding.IsUnavailable).ToArray();
            _batches = ModbusReadPlanner.Plan(availableBindings, _maximumRegisterGap);
            _metrics.SetPlan(_batches.Length, _bindings.Length - availableBindings.Length);
        }

        _metrics.RecordPoll(Stopwatch.GetElapsedTime(startTimestamp), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Applies the bindings whose raw value changed, whose scale factor changed, or that were asked to be reapplied,
    /// then remembers the current raw values.
    /// </summary>
    public void ApplyChanges<TState>(TState state, Action<TState, PropertyReference, object?> apply)
    {
        foreach (var binding in _bindings)
        {
            binding.ChangedThisCycle = binding.HasCurrent &&
                (!binding.HasLast || !binding.CurrentRaw.AsSpan().SequenceEqual(binding.LastRaw));
        }

        foreach (var binding in _bindings)
        {
            if (!ConsumeApplyRequirement(binding) || !TryGetScaleFactorExponent(binding.ScaleFactor, out var exponent))
            {
                continue;
            }

            object? value;
            try
            {
                value = binding.Reader(binding.CurrentRaw, exponent);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to convert Modbus mapping {Path}.", binding.Path);
                continue;
            }

            apply(state, binding.Property, value);
        }

        foreach (var binding in _bindings)
        {
            if (binding.HasCurrent)
            {
                binding.CurrentRaw.CopyTo(binding.LastRaw, 0);
                binding.HasLast = true;
            }
        }
    }

    // Call exactly once per binding per cycle: it consumes the reapply request and may clear HasLast.
    private static bool ConsumeApplyRequirement(ModbusRegisterBinding binding)
    {
        var isScaleFactorChanged = binding.ScaleFactor is { ChangedThisCycle: true };
        if (!binding.HasCurrent)
        {
            if (isScaleFactorChanged)
            {
                // The applied value still uses the old scale factor, so the next read must apply even unchanged words.
                binding.HasLast = false;
            }

            return false;
        }

        var isReapplyRequested = binding.ConsumeReapplyRequest();
        return binding.ChangedThisCycle || isReapplyRequested || isScaleFactorChanged;
    }

    private static bool TryGetScaleFactorExponent(ModbusRegisterBinding? scaleFactor, out int exponent)
    {
        exponent = 0;
        if (scaleFactor is null)
        {
            return true;
        }

        ReadOnlySpan<byte> scaleFactorRaw;
        if (scaleFactor.HasCurrent)
        {
            scaleFactorRaw = scaleFactor.CurrentRaw;
        }
        else if (scaleFactor.HasLast)
        {
            scaleFactorRaw = scaleFactor.LastRaw;
        }
        else
        {
            // Not known yet; the scale factor's first successful read marks it changed and reapplies this.
            return false;
        }

        var attribute = scaleFactor.Attribute;
        if (ModbusRegisterCodec.IsNotAvailable(scaleFactorRaw, attribute.DataType, attribute.WordOrder, attribute.NotAvailableValue))
        {
            // Unknown like an unread one; its next available value is a change that reapplies this.
            return false;
        }

        exponent = (int)ModbusRegisterCodec.ReadInteger(scaleFactorRaw, attribute.DataType, attribute.WordOrder);
        return true;
    }

    /// <summary>
    /// Reads the bindings of a rejected batch one by one and isolates them until the next connect.
    /// </summary>
    private async Task ReadIndividuallyAsync(
        IModbusRegisterReader reader, ModbusReadBatch batch, ModbusResponseException batchException, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(batchException,
                "Modbus read of {Count} {Space} from {Address} (unit {UnitId}) was rejected; reading its mappings one by one from now on.",
                batch.Count, batch.Space, batch.StartAddress, batch.UnitId);
        }

        foreach (var binding in batch.Bindings)
        {
            // Even when every binding reads fine alone (a rejected gap register, or a device rejecting
            // block-crossing reads), merging them again would fail and re-read one by one every cycle.
            binding.IsIsolated = true;
            try
            {
                var data = await reader.ReadAsync(binding.UnitId, binding.Space, binding.Address, binding.Count, cancellationToken).ConfigureAwait(false);
                CopyToBinding(binding, data.Span, binding.Address);
            }
            catch (ModbusResponseException exception) when (exception.IsPermanentRejection)
            {
                binding.IsUnavailable = true;
                _logger.LogWarning(exception,
                    "Modbus mapping {Path} ({Space} {Address}, unit {UnitId}) was rejected with exception code {ExceptionCode} and is not read again until the next connect.",
                    binding.Path, binding.Space, binding.Address, binding.UnitId, exception.ExceptionCode);
            }
            catch (ModbusResponseException exception)
            {
                // Keyed like the request of its own this binding gets from the next cycle on.
                LogFailedRequestOnce((binding.UnitId, binding.Space, binding.Address, binding.Count), binding.Path, exception);
            }
        }
    }

    /// <summary>
    /// Logs a failed request once until it succeeds again.
    /// </summary>
    private void LogFailedRequestOnce(
        (byte UnitId, ModbusAddressSpace Space, int StartAddress, int Count) key, string firstPath, ModbusResponseException exception)
    {
        if (_failingBatches.Add(key))
        {
            _logger.LogWarning(exception,
                "Modbus read of {Count} {Space} from {Address} (unit {UnitId}, first mapping {Path}) failed with exception code {ExceptionCode}.",
                key.Count, key.Space, key.StartAddress, key.UnitId, firstPath, exception.ExceptionCode);
        }
    }

    private static (byte UnitId, ModbusAddressSpace Space, int StartAddress, int Count) GetKey(ModbusReadBatch batch)
        => (batch.UnitId, batch.Space, batch.StartAddress, batch.Count);

    private static void CopyToBinding(ModbusRegisterBinding binding, ReadOnlySpan<byte> data, int startAddress)
    {
        var offset = binding.Address - startAddress;
        if (binding.IsBitSpace)
        {
            binding.CurrentRaw[0] = (byte)((data[offset / 8] >> (offset % 8)) & 1);
        }
        else
        {
            data.Slice(offset * 2, binding.CurrentRaw.Length).CopyTo(binding.CurrentRaw);
        }

        binding.HasCurrent = true;
    }
}
