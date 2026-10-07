using System.Globalization;
using System.Text;
using System.Text.Json;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec.Tests.Testing;

/// <summary>
/// Builds the registers of a SunSpec model chain from model definitions and raw point values.
/// </summary>
internal sealed class SunSpecTestChain
{
    private const ushort PadValue = 0x8000;

    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();

    private readonly List<ushort> _registers = [0x5375, 0x6E53];

    public SunSpecTestChain(int markerAddress = 40000)
    {
        MarkerAddress = markerAddress;
    }

    public int MarkerAddress { get; }

    /// <summary>
    /// Gets the address the next model starts at.
    /// </summary>
    public int NextAddress => MarkerAddress + _registers.Count;

    /// <summary>
    /// Appends a model. Values are raw register values by point name, <c>null</c> or missing for "not implemented"; a
    /// group takes a list of value dictionaries under its group name. <paramref name="length"/> truncates or pads it.
    /// </summary>
    /// <exception cref="ArgumentException">The model has no built-in definition, or a value names no point or group.</exception>
    public SunSpecTestChain AddModel(
        int modelId, IReadOnlyDictionary<string, object?>? values = null, int? length = null, SunSpecModelDefinition? definition = null)
    {
        definition ??= SunSpecDefinitions.TryGetBuiltIn(modelId)
            ?? throw new ArgumentException($"Model {modelId} has no built-in definition.", nameof(modelId));

        var registers = new List<ushort>();
        EncodeGroup(definition.Group, values ?? NoValues, registers, isTopLevel: true);

        var modelLength = length ?? registers.Count - 2;
        if (modelLength + 2 < registers.Count)
        {
            registers.RemoveRange(modelLength + 2, registers.Count - modelLength - 2);
        }

        while (registers.Count < modelLength + 2)
        {
            registers.Add(0);
        }

        registers[0] = (ushort)modelId;
        registers[1] = (ushort)modelLength;
        _registers.AddRange(registers);
        return this;
    }

    /// <summary>
    /// Appends a model's registers as they are, starting with its ID and length registers.
    /// </summary>
    public SunSpecTestChain AddRawModel(IReadOnlyList<ushort> registers)
    {
        _registers.AddRange(registers);
        return this;
    }

    /// <summary>
    /// Gets the registers from the marker to the end model (0xFFFF, 0).
    /// </summary>
    public ushort[] Build() => [.. _registers, 0xFFFF, 0];

    /// <summary>
    /// Creates a chain from a register dump with a <c>markerAddress</c> and <c>models</c> of <c>address</c> and
    /// <c>registers</c>, each model's registers starting with its ID and length registers.
    /// </summary>
    /// <exception cref="InvalidDataException">The models do not follow each other without gaps.</exception>
    public static SunSpecTestChain FromDump(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var chain = new SunSpecTestChain(root.GetProperty("markerAddress").GetInt32());
        foreach (var model in root.GetProperty("models").EnumerateArray())
        {
            if (model.GetProperty("address").GetInt32() != chain.NextAddress)
            {
                throw new InvalidDataException("The dump has a gap between models.");
            }

            chain.AddRawModel(model.GetProperty("registers").EnumerateArray().Select(register => register.GetUInt16()).ToArray());
        }

        return chain;
    }

    private static void EncodeGroup(SunSpecGroupDefinition group, IReadOnlyDictionary<string, object?> values, List<ushort> registers, bool isTopLevel)
    {
        foreach (var key in values.Keys)
        {
            if (group.Points.All(point => point.Name != key) && group.Groups.All(child => child.Name != key))
            {
                throw new ArgumentException($"Group {group.Name} has no point or group named {key}.", nameof(values));
            }
        }

        for (var index = 0; index < group.Points.Count; index++)
        {
            // AddModel writes the ID and L registers of the model itself.
            if (isTopLevel && index < 2)
            {
                registers.Add(0);
                continue;
            }

            var point = group.Points[index];
            EncodePoint(point, values.GetValueOrDefault(point.Name), registers);
        }

        foreach (var child in group.Groups)
        {
            foreach (var instance in GetInstanceValues(child, values.GetValueOrDefault(child.Name)))
            {
                EncodeGroup(child, instance, registers, isTopLevel: false);
            }
        }
    }

    // Without values, a single or fixed-count group still takes its registers; other groups have no instances.
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> GetInstanceValues(SunSpecGroupDefinition group, object? values) => values switch
    {
        null => Enumerable.Repeat(NoValues, group.Count.IsSingle ? 1 : group.Count.Fixed ?? 0).ToArray(),
        IReadOnlyList<IReadOnlyDictionary<string, object?>> list => list,
        IReadOnlyDictionary<string, object?> single => [single],
        _ => throw new ArgumentException($"Group {group.Name} takes a list of value dictionaries, not {values.GetType().Name}.", nameof(values))
    };

    // Encodes with the library's own point mapping, so a value reads back exactly as the library decodes it.
    private static void EncodePoint(SunSpecPointDefinition point, object? value, List<ushort> registers)
    {
        var map = SunSpecPointMapping.TryMap(point);
        if (map?.DataType == ModbusDataType.String)
        {
            EncodeText(point, value, registers);
            return;
        }

        ulong raw;
        if (map?.DataType == ModbusDataType.F32)
        {
            raw = BitConverter.SingleToUInt32Bits(value is null ? float.NaN : Convert.ToSingle(value, CultureInfo.InvariantCulture));
        }
        else if (value is not null)
        {
            raw = ToRaw(value);
        }
        else if (map is null)
        {
            // Points without a mapping (pad, ipv6addr) keep their SunSpec fill value.
            raw = point.Type == "pad" ? PadValue : 0UL;
        }
        else
        {
            raw = GetNotAvailableValue(map.NotAvailableValue, point.Size);
        }

        for (var index = point.Size - 1; index >= 0; index--)
        {
            registers.Add(index < 4 ? (ushort)(raw >> (16 * index)) : (ushort)0);
        }
    }

    private static void EncodeText(SunSpecPointDefinition point, object? value, List<ushort> registers)
    {
        var bytes = new byte[point.Size * 2];
        if (value is string text)
        {
            if (Encoding.ASCII.GetByteCount(text) > bytes.Length)
            {
                throw new ArgumentException($"The text of point {point.Name} exceeds {bytes.Length} bytes.", nameof(value));
            }

            Encoding.ASCII.GetBytes(text, bytes);
        }
        else if (value is not null)
        {
            throw new ArgumentException($"Point {point.Name} takes a string, not {value.GetType().Name}.", nameof(value));
        }

        for (var index = 0; index < point.Size; index++)
        {
            registers.Add((ushort)((bytes[index * 2] << 8) | bytes[index * 2 + 1]));
        }
    }

    private static ulong ToRaw(object value) => value switch
    {
        ulong unsigned => unsigned,
        double or float or decimal => throw new ArgumentException("Integer points take raw integer values.", nameof(value)),
        _ => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture))
    };

    private static ulong GetNotAvailableValue(ModbusNotAvailableValue notAvailableValue, int size)
    {
        var bits = 16 * size;
        var signBit = 1UL << (bits - 1);
        return notAvailableValue switch
        {
            ModbusNotAvailableValue.SignedMinimum => signBit,
            ModbusNotAvailableValue.SignedMaximum => signBit - 1,
            ModbusNotAvailableValue.UnsignedMaximum => signBit | (signBit - 1),
            _ => 0
        };
    }
}
