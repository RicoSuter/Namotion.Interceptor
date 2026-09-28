using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.Luxtronik.Gating;

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
                !IsSupported(gate.MinimumFirmwareVersion, gate.Feature, firmwareVersion, configuredFeatures))
            {
                return false;
            }
        }

        return property.Subject is not ILuxtronikGatedSubject subject ||
            IsSupported(subject.MinimumFirmwareVersion, subject.Feature, firmwareVersion, configuredFeatures);
    }

    /// <summary>
    /// Checks a firmware and feature requirement. <c>null</c> configured features means the controller does not report them, so feature gates pass.
    /// </summary>
    public static bool IsSupported(
        Version? minimumFirmwareVersion, LuxtronikFeature feature, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        if (minimumFirmwareVersion is not null && firmwareVersion < minimumFirmwareVersion)
        {
            return false;
        }

        return feature == LuxtronikFeature.None || configuredFeatures is null || configuredFeatures.Contains(feature);
    }

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
}
