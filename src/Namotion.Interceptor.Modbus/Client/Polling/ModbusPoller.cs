using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Client.Transport;
using Namotion.Interceptor.Modbus.Mapping;

namespace Namotion.Interceptor.Modbus.Client.Polling;

/// <summary>
/// Runs the read cycle of one connection. Not thread-safe except <see cref="RequestReapply"/>: the source calls
/// <see cref="ReadAsync"/> and <see cref="ApplyChanges{TState}"/> strictly one after another.
/// </summary>
internal sealed class ModbusPoller
{
    private readonly ModbusRegisterBinding[] _bindings;
    private readonly Dictionary<PropertyReference, ModbusRegisterBinding> _bindingsByProperty;
    private readonly int _maximumRegisterGap;
    private readonly ISubjectSource _source;
    private readonly ModbusPollingMetrics _metrics;
    private readonly ILogger _logger;

    // Keyed by request rather than batch instance so a replan does not log a still failing request again.
    private const int MismatchWarningThreshold = 3;

    private readonly HashSet<(byte UnitId, ModbusAddressSpace AddressSpace, int StartAddress, int Count)> _failingBatches = [];
    private ModbusReadBatch[] _batches;

    public ModbusPoller(
        IReadOnlyCollection<ModbusRegisterBinding> bindings, int maximumRegisterGap,
        ISubjectSource source, ModbusPollingMetrics metrics, ILogger logger)
    {
        _bindings = bindings.ToArray();
        _bindingsByProperty = _bindings.ToDictionary(binding => binding.Property, PropertyReference.Comparer);
        _maximumRegisterGap = maximumRegisterGap;
        _source = source;
        _metrics = metrics;
        _logger = logger;
        _batches = ModbusReadPlanner.Plan(_bindings, maximumRegisterGap);
        _metrics.SetPlan(CountRequests(_batches), unavailablePropertyCount: 0);
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
        var hasReadData = false;
        foreach (var batch in _batches)
        {
            try
            {
                if (batch.RequestCount == 1)
                {
                    var data = await reader.ReadAsync(batch.UnitId, batch.AddressSpace, batch.StartAddress, batch.Count, cancellationToken).ConfigureAwait(false);

                    // The reader may hand out a pooled buffer that its next read overwrites, so copy before reading on.
                    CopyToBindings(batch, data.Span);
                }
                else
                {
                    await ReadSplitBindingAsync(reader, batch, cancellationToken).ConfigureAwait(false);
                }

                hasReadData = true;

                if (_failingBatches.Count > 0 && _failingBatches.Remove(GetKey(batch)))
                {
                    _logger.LogInformation(
                        "Modbus read of {AddressSpace} {Address} (unit {UnitId}) succeeds again.",
                        batch.AddressSpace, batch.StartAddress, batch.UnitId);
                }
            }
            catch (ModbusResponseException exception) when (exception.IsPermanentRejection)
            {
                _metrics.RecordFailedRequest();
                _failingBatches.Remove(GetKey(batch));
                hasReadData |= await HandlePermanentRejectionAsync(reader, batch, exception, cancellationToken).ConfigureAwait(false);
                isReplanRequired = true;
            }
            catch (ModbusResponseException exception)
            {
                // Skipped for this cycle only: its values keep their last value and the plan stays as it is.
                _metrics.RecordFailedRequest();
                LogFailedRequestOnce(GetKey(batch), batch.Bindings[0].Path, exception);
            }
        }

        if (isReplanRequired)
        {
            var availableBindings = _bindings.Where(binding => !binding.IsUnavailable).ToArray();
            _batches = ModbusReadPlanner.Plan(availableBindings, _maximumRegisterGap);
            _metrics.SetPlan(CountRequests(_batches), _bindings.Length - availableBindings.Length);
        }

        _metrics.RecordPoll(Stopwatch.GetElapsedTime(startTimestamp), DateTimeOffset.UtcNow, hasReadData);
    }

    /// <summary>
    /// Applies the bindings whose raw value changed, whose scale factor changed, or that were asked to be reapplied,
    /// then remembers the current raw values. Skips the bindings whose property the source no longer owns.
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
            if (!ConsumeApplyRequirement(binding) || !IsOwned(binding) || !TryGetScaleFactorExponent(binding.ScaleFactor, out var exponent))
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

    // Detaching a subject releases its claims while its bindings stay in the plan until the next connect.
    private bool IsOwned(ModbusRegisterBinding binding)
        => binding.Property.TryGetSource(out var owner) && ReferenceEquals(owner, _source);

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
    /// Reads the single binding of a batch larger than one request in consecutive requests. Modbus cannot read them
    /// atomically, so a value that differs from the last one is read a second time and only becomes current when both
    /// reads agree; otherwise the binding keeps its value and is read again next cycle. A failed request propagates
    /// and leaves the binding without a current value.
    /// </summary>
    private async Task ReadSplitBindingAsync(IModbusRegisterReader reader, ModbusReadBatch batch, CancellationToken cancellationToken)
    {
        Debug.Assert(batch.Bindings.Length == 1, "A batch of several requests holds exactly one binding.");
        var binding = batch.Bindings[0];
        for (var index = 0; index < batch.RequestCount; index++)
        {
            var (address, count) = batch.GetRequest(index);
            var data = await reader.ReadAsync(batch.UnitId, batch.AddressSpace, address, count, cancellationToken).ConfigureAwait(false);
            data.Span[..(count * 2)].CopyTo(binding.CurrentRaw.AsSpan((address - binding.Address) * 2));
        }

        if (binding.HasLast && binding.CurrentRaw.AsSpan().SequenceEqual(binding.LastRaw))
        {
            MarkConfirmed(binding);
            return;
        }

        for (var index = 0; index < batch.RequestCount; index++)
        {
            var (address, count) = batch.GetRequest(index);
            var data = await reader.ReadAsync(batch.UnitId, batch.AddressSpace, address, count, cancellationToken).ConfigureAwait(false);
            if (!data.Span[..(count * 2)].SequenceEqual(binding.CurrentRaw.AsSpan((address - binding.Address) * 2, count * 2)))
            {
                RecordMismatch(binding);
                return;
            }
        }

        MarkConfirmed(binding);
    }

    private static void MarkConfirmed(ModbusRegisterBinding binding)
    {
        binding.HasCurrent = true;
        binding.ConsecutiveMismatchCount = 0;
    }

    /// <summary>
    /// Logs a warning once the two reads of a binding disagreed in <see cref="MismatchWarningThreshold"/> cycles in a row,
    /// and again only after a value was confirmed in between.
    /// </summary>
    private void RecordMismatch(ModbusRegisterBinding binding)
    {
        // Saturates at the threshold, so the warning is logged once per streak.
        if (binding.ConsecutiveMismatchCount < MismatchWarningThreshold)
        {
            binding.ConsecutiveMismatchCount++;
            if (binding.ConsecutiveMismatchCount == MismatchWarningThreshold)
            {
                _logger.LogWarning(
                    "Modbus mapping {Path} ({AddressSpace} {Address} to {LastAddress}, unit {UnitId}) changed between its two reads in {CycleCount} cycles in a row and keeps its previous value until two reads agree.",
                    binding.Path, binding.AddressSpace, binding.Address, binding.Address + binding.Count - 1, binding.UnitId, MismatchWarningThreshold);
                return;
            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Modbus mapping {Path} changed while it was read; it is read again next cycle.", binding.Path);
        }
    }

    /// <summary>
    /// Marks the binding of a rejected single-binding batch unavailable, or reads the bindings of a larger one one by one.
    /// Returns whether any binding was read.
    /// </summary>
    private Task<bool> HandlePermanentRejectionAsync(
        IModbusRegisterReader reader, ModbusReadBatch batch, ModbusResponseException exception, CancellationToken cancellationToken)
    {
        if (batch.Bindings.Length == 1)
        {
            MarkUnavailable(batch.Bindings[0], exception);
            return Task.FromResult(false);
        }

        return ReadIndividuallyAsync(reader, batch, exception, cancellationToken);
    }

    /// <summary>
    /// Reads the bindings of a rejected batch one by one and isolates them until the next connect.
    /// Returns whether any binding was read.
    /// </summary>
    private async Task<bool> ReadIndividuallyAsync(
        IModbusRegisterReader reader, ModbusReadBatch batch, ModbusResponseException batchException, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(batchException,
                "Modbus read of {Count} {AddressSpace} from {Address} (unit {UnitId}) was rejected; reading its mappings one by one from now on.",
                batch.Count, batch.AddressSpace, batch.StartAddress, batch.UnitId);
        }

        var hasReadData = false;
        foreach (var binding in batch.Bindings)
        {
            // Even when every binding reads fine alone (a rejected gap register, or a device rejecting
            // block-crossing reads), merging them again would fail and re-read one by one every cycle.
            binding.IsIsolated = true;
            try
            {
                var data = await reader.ReadAsync(binding.UnitId, binding.AddressSpace, binding.Address, binding.Count, cancellationToken).ConfigureAwait(false);
                CopyToBinding(binding, data.Span, binding.Address);
                hasReadData = true;
            }
            catch (ModbusResponseException exception) when (exception.IsPermanentRejection)
            {
                MarkUnavailable(binding, exception);
            }
            catch (ModbusResponseException exception)
            {
                // Keyed like the request of its own this binding gets from the next cycle on.
                LogFailedRequestOnce((binding.UnitId, binding.AddressSpace, binding.Address, binding.Count), binding.Path, exception);
            }
        }

        return hasReadData;
    }

    private void MarkUnavailable(ModbusRegisterBinding binding, ModbusResponseException exception)
    {
        binding.IsUnavailable = true;
        _logger.LogWarning(exception,
            "Modbus mapping {Path} ({AddressSpace} {Address}, unit {UnitId}) was rejected with exception code {ExceptionCode} and is not read again until the next connect.",
            binding.Path, binding.AddressSpace, binding.Address, binding.UnitId, exception.ExceptionCode);
    }

    /// <summary>
    /// Logs a failed request once until it succeeds again.
    /// </summary>
    private void LogFailedRequestOnce(
        (byte UnitId, ModbusAddressSpace AddressSpace, int StartAddress, int Count) key, string firstPath, ModbusResponseException exception)
    {
        if (_failingBatches.Add(key))
        {
            _logger.LogWarning(exception,
                "Modbus read of {Count} {AddressSpace} from {Address} (unit {UnitId}, first mapping {Path}) failed with exception code {ExceptionCode}.",
                key.Count, key.AddressSpace, key.StartAddress, key.UnitId, firstPath, exception.ExceptionCode);
        }
    }

    private static int CountRequests(ModbusReadBatch[] batches)
    {
        var count = 0;
        foreach (var batch in batches)
        {
            count += batch.RequestCount;
        }

        return count;
    }

    private static (byte UnitId, ModbusAddressSpace AddressSpace, int StartAddress, int Count) GetKey(ModbusReadBatch batch)
        => (batch.UnitId, batch.AddressSpace, batch.StartAddress, batch.Count);

    private static void CopyToBindings(ModbusReadBatch batch, ReadOnlySpan<byte> data)
    {
        foreach (var binding in batch.Bindings)
        {
            CopyToBinding(binding, data, batch.StartAddress);
        }
    }

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
