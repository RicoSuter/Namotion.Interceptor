using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.Luxtronik.Gating;

/// <summary>
/// Decides whether a register is read, from the controller firmware and its active functions.
/// </summary>
internal static class LuxtronikGating
{
    public const int FeatureFlagCount = 12;

    /// <summary>
    /// Gets firmware version 3.92.0, the gate of the registers added in that release.
    /// </summary>
    public static readonly Version Firmware392 = new(3, 92, 0);

    /// <summary>
    /// Gets whether <paramref name="property"/> is read: its register gates and its subject's gate must all pass.
    /// On an <see cref="ILuxtronikCircuitSubject"/>, register feature gates are shifted to the circuit's own feature.
    /// </summary>
    public static bool IsSupported(
        RegisteredSubjectProperty property, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        var featureOffset = property.Subject is ILuxtronikCircuitSubject circuit ? circuit.FeatureOffset : 0;
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ILuxtronikRegisterGate gate &&
                !IsSupported(gate.MinimumFirmwareVersion, Shift(gate.Feature, featureOffset), firmwareVersion, configuredFeatures))
            {
                return false;
            }
        }

        return property.Subject is not ILuxtronikGatedSubject subject ||
            IsSupported(subject.MinimumFirmwareVersion, subject.Feature, firmwareVersion, configuredFeatures);
    }

    /// <summary>
    /// Gets whether a gate passes: the firmware is at least <paramref name="minimumFirmwareVersion"/>, and
    /// <paramref name="feature"/> is active. <c>null</c> configured features means the controller does not
    /// report them, so feature gates pass.
    /// </summary>
    public static bool IsSupported(
        Version? minimumFirmwareVersion,
        LuxtronikFeature feature,
        Version firmwareVersion,
        IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        if (minimumFirmwareVersion is not null && firmwareVersion < minimumFirmwareVersion)
        {
            return false;
        }

        return feature == LuxtronikFeature.None || configuredFeatures is null || configuredFeatures.Contains(feature);
    }

    /// <summary>
    /// Gets whether <paramref name="property"/> is a mapped register that can hold <c>null</c>.
    /// </summary>
    public static bool IsNullableRegister(RegisteredSubjectProperty property)
    {
        if (property.Type.IsValueType && Nullable.GetUnderlyingType(property.Type) is null)
        {
            return false;
        }

        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ModbusRegisterAttribute)
            {
                return true;
            }
        }

        return false;
    }

    private static LuxtronikFeature Shift(LuxtronikFeature feature, int offset)
        => feature == LuxtronikFeature.None ? feature : feature + offset;

    /// <summary>
    /// Gets the functions whose flag is set; the flag index is the <see cref="LuxtronikFeature"/> value.
    /// </summary>
    public static HashSet<LuxtronikFeature> GetConfiguredFeatures(bool[] flags)
    {
        var features = new HashSet<LuxtronikFeature>();
        for (var index = 0; index < Math.Min(flags.Length, FeatureFlagCount); index++)
        {
            if (flags[index])
            {
                features.Add((LuxtronikFeature)index);
            }
        }

        return features;
    }

    /// <summary>
    /// Gets the set flags as a bit mask; the bit index is the <see cref="LuxtronikFeature"/> value.
    /// </summary>
    public static int GetFeatureMask(ReadOnlySpan<bool> flags)
    {
        var mask = 0;
        for (var index = 0; index < Math.Min(flags.Length, FeatureFlagCount); index++)
        {
            if (flags[index])
            {
                mask |= 1 << index;
            }
        }

        return mask;
    }

    /// <summary>
    /// Gets the names of the functions whose bit is set in <paramref name="mask"/>, separated by commas.
    /// </summary>
    public static string GetFeatureNames(int mask)
    {
        var names = new List<string>();
        for (var index = 0; index < FeatureFlagCount; index++)
        {
            if ((mask & (1 << index)) != 0)
            {
                names.Add(((LuxtronikFeature)index).ToString());
            }
        }

        return string.Join(", ", names);
    }
}
