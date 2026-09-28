namespace Namotion.Interceptor.Modbus.Mapping;

/// <summary>
/// Merges bindings of the same unit and address space into contiguous read requests.
/// </summary>
internal static class ModbusReadPlanner
{
    public const int MaximumRegistersPerRequest = 125;
    public const int MaximumBitsPerRequest = 2000;

    /// <summary>
    /// Plans the read requests. Bindings may overlap. An <see cref="ModbusRegisterBinding.IsIsolated"/> binding
    /// always gets a request of its own.
    /// </summary>
    public static ModbusReadBatch[] Plan(IEnumerable<ModbusRegisterBinding> bindings, int maximumGap)
    {
        var sorted = bindings.ToArray();
        if (sorted.Length == 0)
        {
            return [];
        }

        Array.Sort(sorted, CompareBindings);

        var batches = new List<ModbusReadBatch>();
        var batchStartIndex = 0;
        var start = sorted[0].Address;
        var end = start + sorted[0].Count;

        for (var index = 1; index < sorted.Length; index++)
        {
            var previous = sorted[index - 1];
            var binding = sorted[index];
            var bindingEnd = binding.Address + binding.Count;
            var mergedEnd = Math.Max(end, bindingEnd);

            if (CanShareRequest(previous, binding) &&
                binding.Address - end <= maximumGap &&
                mergedEnd - start <= GetLimit(binding.Space))
            {
                end = mergedEnd;
                continue;
            }

            batches.Add(CreateBatch(sorted, batchStartIndex, index, start, end));
            batchStartIndex = index;
            start = binding.Address;
            end = bindingEnd;
        }

        batches.Add(CreateBatch(sorted, batchStartIndex, sorted.Length, start, end));
        return batches.ToArray();
    }

    private static bool CanShareRequest(ModbusRegisterBinding previous, ModbusRegisterBinding binding)
        => binding.UnitId == previous.UnitId &&
           binding.Space == previous.Space &&
           !binding.IsIsolated &&
           !previous.IsIsolated;

    private static int GetLimit(ModbusAddressSpace space)
        => space.IsBitSpace()
            ? MaximumBitsPerRequest
            : MaximumRegistersPerRequest;

    private static ModbusReadBatch CreateBatch(ModbusRegisterBinding[] sorted, int startIndex, int endIndex, int start, int end)
    {
        var first = sorted[startIndex];
        return new ModbusReadBatch(first.UnitId, first.Space, start, end - start, sorted[startIndex..endIndex]);
    }

    private static int CompareBindings(ModbusRegisterBinding left, ModbusRegisterBinding right)
    {
        var result = left.UnitId.CompareTo(right.UnitId);
        if (result == 0)
        {
            result = ((int)left.Space).CompareTo((int)right.Space);
        }

        if (result == 0)
        {
            result = left.Address.CompareTo(right.Address);
        }

        if (result == 0)
        {
            result = left.Count.CompareTo(right.Count);
        }

        // Isolated bindings sort last among equal keys so they never split mergeable neighbors.
        return result != 0 ? result : left.IsIsolated.CompareTo(right.IsIsolated);
    }
}
