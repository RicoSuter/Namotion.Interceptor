using Namotion.Devices.Luxtronik.Enums;
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
    /// Checks the property's register attribute gate and its subject's gate; both must pass.
    /// </summary>
    public static bool IsSupported(
        RegisteredSubjectProperty property, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ILuxtronikRegisterGate gate &&
                !IsSupported(gate.MinimumFirmwareVersion, gate.Feature, LuxtronikFeature.None, firmwareVersion, configuredFeatures))
            {
                return false;
            }
        }

        return property.Subject is not ILuxtronikGatedSubject subject ||
            IsSupported(subject.MinimumFirmwareVersion, subject.Feature, subject.AlternativeFeature, firmwareVersion, configuredFeatures);
    }

    /// <summary>
    /// Checks a firmware requirement and a feature requirement that <paramref name="feature"/> or
    /// <paramref name="alternativeFeature"/> satisfies. <c>null</c> configured features means the controller does not
    /// report them, so feature gates pass.
    /// </summary>
    public static bool IsSupported(
        Version? minimumFirmwareVersion,
        LuxtronikFeature feature,
        LuxtronikFeature alternativeFeature,
        Version firmwareVersion,
        IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        if (minimumFirmwareVersion is not null && firmwareVersion < minimumFirmwareVersion)
        {
            return false;
        }

        if (feature == LuxtronikFeature.None || configuredFeatures is null)
        {
            return true;
        }

        return configuredFeatures.Contains(feature) ||
            (alternativeFeature != LuxtronikFeature.None && configuredFeatures.Contains(alternativeFeature));
    }

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
