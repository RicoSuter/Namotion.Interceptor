extern alias SunSpecGenerator;

using System.Globalization;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using SunSpecGenerator::Namotion.Devices.SunSpec.Generator;

namespace Namotion.Devices.SunSpec.Tests;

/// <summary>
/// Serves every built-in model with a value in each point and checks that the device reads each value back. The
/// expected values come from the JSON definitions only, so the register offsets and scale factor wiring of the
/// generated classes are checked independently of the generator.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SunSpecIntegrationCollection.Name)]
public class SunSpecModelRoundTripTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<int> GeneratedModelIds
    {
        get
        {
            // The test models 63001 and 63002 have no generated class.
            var data = new TheoryData<int>();
            foreach (var modelId in SunSpecDefinitions.GetBuiltInModelIds().Where(SunSpecModelFactory.IsGenerated))
            {
                data.Add(modelId);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GeneratedModelIds))]
    public async Task WhenBuiltInModelIsServed_ThenEveryPointIsReadAsWritten(int modelId)
    {
        // Arrange
        var definition = SunSpecDefinitions.TryGetBuiltIn(modelId)!;
        var values = new RawValueGenerator(definition).CreateModelValues();
        var chain = modelId == 1
            ? new SunSpecTestChain().AddModel(1, values)
            : new SunSpecTestChain().AddModel(1, new Dictionary<string, object?> { ["Mn"] = "RoundTrip" }).AddModel(modelId, values);
        using var server = new SunSpecTestServer();
        server.Start((1, chain));

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);

        // Assert
        IReadOnlyList<string> mismatches = [];
        bool IsReadAsWritten()
        {
            mismatches = Verify(host.Device, definition, values);
            return mismatches.Count == 0;
        }

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(IsReadAsWritten, WaitTimeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"Model {modelId} is not read as written (device: {host.Device.Status}, {host.Device.StatusMessage}):{Environment.NewLine}" +
                string.Join(Environment.NewLine, mismatches));
        }
    }

    private static List<string> Verify(SunSpecDevice device, SunSpecModelDefinition definition, IReadOnlyDictionary<string, object?> values)
    {
        var logicalDevice = device.Units.GetValueOrDefault(1)?.Devices.FirstOrDefault();
        var model = definition.Id == 1
            ? logicalDevice?.Common
            : logicalDevice?.Models.FirstOrDefault(candidate => candidate.ModelId == definition.Id);

        if (model is null)
        {
            return [$"Model {definition.Id}: not discovered."];
        }

        var mismatches = new List<string>();
        var path = $"Model {definition.Id} {model.GetType().Name}";
        var classDefinition = GetClassDefinition(definition.Id);
        if (!HaveSamePointCounts(definition.Group, classDefinition.Group))
        {
            return [$"{path}: the class representative {classDefinition.Id} has different points or groups."];
        }

        if (model.ModelIdRegister != definition.Id)
        {
            mismatches.Add($"{path}.{nameof(ISunSpecModel.ModelIdRegister)}: expected {definition.Id}, actual {Format(model.ModelIdRegister)}");
        }

        VerifyGroup(model, definition.Group, classDefinition.Group, new Scope(values, null), path, isTopLevel: true, mismatches);
        return mismatches;
    }

    // The models of a family share the class generated from their representative, and so its point names; their
    // registers are still encoded and decoded from their own definition.
    private static SunSpecModelDefinition GetClassDefinition(int modelId)
    {
        var classOverride = SunSpecGeneratorOverrides.Load().Classes.FirstOrDefault(candidate => candidate.Models.Contains(modelId));
        return SunSpecDefinitions.TryGetBuiltIn(classOverride?.Representative ?? modelId)!;
    }

    private static bool HaveSamePointCounts(SunSpecGroupDefinition group, SunSpecGroupDefinition classGroup)
        => group.Points.Count == classGroup.Points.Count
           && group.Groups.Count == classGroup.Groups.Count
           && group.Groups.Zip(classGroup.Groups).All(pair => HaveSamePointCounts(pair.First, pair.Second));

    private static void VerifyGroup(
        IInterceptorSubject subject, SunSpecGroupDefinition group, SunSpecGroupDefinition classGroup, Scope scope, string path, bool isTopLevel, List<string> mismatches)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            mismatches.Add($"{path}: not registered.");
            return;
        }

        for (var index = isTopLevel ? 2 : 0; index < group.Points.Count; index++)
        {
            var point = group.Points[index];
            if (SunSpecPointMapping.TryMap(point) is not { } map)
            {
                continue;
            }

            var propertyName = classGroup.Points[index].Name;
            var property = registeredSubject.TryGetProperty(propertyName);
            if (property is null)
            {
                mismatches.Add($"{path}.{propertyName}: no property.");
                continue;
            }

            var expected = GetExpectedValue(map, scope.Values.GetValueOrDefault(point.Name), scope, out var error);
            var actual = property.GetValue();
            if (error is not null)
            {
                mismatches.Add($"{path}.{propertyName}: {error}");
            }
            else if (!Equals(expected, Normalize(actual)))
            {
                mismatches.Add($"{path}.{propertyName}: expected {Format(expected)}, actual {Format(actual)}");
            }
        }

        for (var groupIndex = 0; groupIndex < group.Groups.Count; groupIndex++)
        {
            var child = group.Groups[groupIndex];
            VerifyChildGroup(registeredSubject, child, classGroup.Groups[groupIndex], scope, path, mismatches);
        }
    }

    private static void VerifyChildGroup(
        RegisteredSubject parent, SunSpecGroupDefinition group, SunSpecGroupDefinition classGroup, Scope scope, string path, List<string> mismatches)
    {
        var propertyName = SunSpecNames.ToPascalCase(group.Name);
        var instances = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)scope.Values[group.Name]!;
        var property = parent.TryGetProperty(propertyName);
        if (property is null)
        {
            mismatches.Add($"{path}.{propertyName}: no property.");
            return;
        }

        IInterceptorSubject[] subjects = property.GetValue() switch
        {
            IInterceptorSubject single => [single],
            IEnumerable<IInterceptorSubject> many => [.. many],
            _ => []
        };

        if (subjects.Length != instances.Count)
        {
            mismatches.Add($"{path}.{propertyName}: expected {instances.Count} instances, actual {subjects.Length}");
            return;
        }

        for (var index = 0; index < subjects.Length; index++)
        {
            VerifyGroup(subjects[index], group, classGroup, new Scope(instances[index], scope), $"{path}.{propertyName}[{index}]", isTopLevel: false, mismatches);
        }
    }

    private static object? GetExpectedValue(SunSpecPointMap map, object? raw, Scope scope, out string? error)
    {
        error = null;
        if (raw is null)
        {
            return null;
        }

        switch (map.Kind)
        {
            case SunSpecValueKind.Text:
                return DecodeText((string)raw);

            case SunSpecValueKind.Number or SunSpecValueKind.Duration:
                var value = raw is float single
                    ? decimal.Parse(single.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture)
                    : Convert.ToDecimal(raw, CultureInfo.InvariantCulture);

                value *= map.Scale;
                if (map.ScaleFactorPointName is { } scaleFactorPointName)
                {
                    if (!scope.TryFind(scaleFactorPointName, out var exponent))
                    {
                        error = $"scale factor point {scaleFactorPointName} is not in an enclosing group.";
                        return null;
                    }

                    value *= PowerOfTen(Convert.ToInt32(exponent, CultureInfo.InvariantCulture));
                }

                return map.Kind == SunSpecValueKind.Duration ? TimeSpan.FromTicks((long)(value * TimeSpan.TicksPerSecond)) : value;

            default:
                return Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
        }
    }

    // Like a Modbus string register: a NUL ends the text and trailing spaces are padding.
    private static string DecodeText(string text)
    {
        var terminator = text.IndexOf('\0', StringComparison.Ordinal);
        return (terminator >= 0 ? text[..terminator] : text).TrimEnd(' ');
    }

    private static decimal PowerOfTen(int exponent)
    {
        var result = 1m;
        for (var index = 0; index < Math.Abs(exponent); index++)
        {
            result = exponent < 0 ? result / 10m : result * 10m;
        }

        return result;
    }

    // Integers, enumerations and bit fields compare by numeric value.
    private static object? Normalize(object? value) => value switch
    {
        Enum => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        short number => (decimal)number,
        ushort number => (decimal)number,
        int number => (decimal)number,
        uint number => (decimal)number,
        long number => (decimal)number,
        ulong number => (decimal)number,
        _ => value
    };

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>
    /// The raw values of a group instance and of its enclosing instances, where scale factor points are looked up.
    /// </summary>
    private sealed record Scope(IReadOnlyDictionary<string, object?> Values, Scope? Parent)
    {
        public bool TryFind(string pointName, out object? value)
        {
            for (var scope = this; scope is not null; scope = scope.Parent)
            {
                if (scope.Values.TryGetValue(pointName, out value))
                {
                    return true;
                }
            }

            value = null;
            return false;
        }
    }

    /// <summary>
    /// Creates distinct, in-range raw values for every point of a model: two instances for each counted or filling
    /// group, small exponents for scale factors, defined symbols for enumerations and bit fields, and one point left
    /// "not implemented".
    /// </summary>
    private sealed class RawValueGenerator(SunSpecModelDefinition definition)
    {
        private const int CountedInstances = 2;

        private static readonly int[] ScaleFactorExponents = [-1, -2, 0, 1];

        private readonly HashSet<string> _countPointNames = GetCountPointNames(definition.Group).ToHashSet(StringComparer.Ordinal);
        private int _sequence;
        private bool _hasNotImplementedPoint;

        public Dictionary<string, object?> CreateModelValues() => CreateGroupValues(definition.Group, isTopLevel: true);

        private Dictionary<string, object?> CreateGroupValues(SunSpecGroupDefinition group, bool isTopLevel)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);

            // The chain writes the ID and L points of the model itself.
            for (var index = isTopLevel ? 2 : 0; index < group.Points.Count; index++)
            {
                var point = group.Points[index];
                if (SunSpecPointMapping.TryMap(point) is { } map)
                {
                    values[point.Name] = CreateRawValue(point, map);
                }
            }

            foreach (var child in group.Groups)
            {
                values[child.Name] = Enumerable.Range(0, GetInstanceCount(child))
                    .Select(IReadOnlyDictionary<string, object?> (_) => CreateGroupValues(child, isTopLevel: false))
                    .ToArray();
            }

            return values;
        }

        private object? CreateRawValue(SunSpecPointDefinition point, SunSpecPointMap map)
        {
            var sequence = ++_sequence;
            if (_countPointNames.Contains(point.Name))
            {
                return CountedInstances;
            }

            if (map.Kind == SunSpecValueKind.ScaleFactor)
            {
                return ScaleFactorExponents[sequence % ScaleFactorExponents.Length];
            }

            if (!_hasNotImplementedPoint && map.NotAvailableValue != ModbusNotAvailableValue.None && map.Kind is SunSpecValueKind.Number or SunSpecValueKind.Integer)
            {
                _hasNotImplementedPoint = true;
                return null;
            }

            return map.Kind switch
            {
                SunSpecValueKind.Text => CreateText(point, sequence),
                SunSpecValueKind.Enumeration => SelectSymbol(point, map, sequence),
                SunSpecValueKind.Flags => CombineBits(point, map, sequence),
                _ => CreateNumber(map.RawType, sequence)
            };
        }

        // Signed values alternate their sign; 32 and 64-bit values use their high registers.
        private static object CreateNumber(Type rawType, int sequence)
        {
            var sign = sequence % 2 == 1 ? -1 : 1;
            return Type.GetTypeCode(rawType) switch
            {
                TypeCode.Single => (object)(float)(sign * (sequence + 0.25)),
                TypeCode.Int16 => sign * (100 + sequence),
                TypeCode.UInt16 => 200 + sequence,
                TypeCode.Int32 => sign * (70_000L + sequence),
                TypeCode.UInt32 => 70_000L + (3L * sequence),
                TypeCode.Int64 => sign * (5_000_000_000L + sequence),
                _ => 5_000_000_000UL + (ulong)sequence
            };
        }

        private static int GetInstanceCount(SunSpecGroupDefinition group)
        {
            if (group.Count.IsSingle)
            {
                return 1;
            }

            return group.Count.Fixed is > 0 ? group.Count.Fixed.Value : CountedInstances;
        }

        private static string CreateText(SunSpecPointDefinition point, int sequence)
        {
            var text = $"{sequence}:{point.Name}";
            return text.Length > point.Size * 2 ? text[..(point.Size * 2)] : text;
        }

        private static long SelectSymbol(SunSpecPointDefinition point, SunSpecPointMap map, int sequence)
        {
            var notImplemented = map.RawType == typeof(ushort) ? ushort.MaxValue : uint.MaxValue;
            var symbols = point.Symbols.Where(symbol => symbol.Value != notImplemented).ToArray();
            return symbols[sequence % symbols.Length].Value;
        }

        private static ulong CombineBits(SunSpecPointDefinition point, SunSpecPointMap map, int sequence)
        {
            var bitCount = map.RawType == typeof(ushort) ? 16 : 32;
            var bits = point.Symbols.Where(symbol => symbol.Value >= 0 && symbol.Value < bitCount).Select(symbol => (int)symbol.Value).Distinct().ToArray();
            return (1UL << bits[sequence % bits.Length]) | (1UL << bits[(sequence + 1) % bits.Length]);
        }

        private static IEnumerable<string> GetCountPointNames(SunSpecGroupDefinition group)
            => group.Groups.SelectMany(child => GetCountPointNames(child).Prepend(child.Count.PointName)).OfType<string>();
    }
}
