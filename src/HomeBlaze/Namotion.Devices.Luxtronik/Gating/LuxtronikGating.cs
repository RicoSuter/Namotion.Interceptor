using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.Luxtronik;

internal static class LuxtronikGating
{
    public const int FeatureFlagCount = 12;

    /// <summary>
    /// Checks the property's register attribute gate and its subject's gate.
    /// </summary>
    public static bool IsSupported(
        RegisteredSubjectProperty property, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ILuxtronikRegisterGate gate &&
                !IsSupportedCore(gate.MinimumFirmwareVersion, gate.Feature, firmwareVersion, configuredFeatures))
            {
                return false;
            }
        }

        return property.Subject is not ILuxtronikGatedSubject subject ||
            IsSupported(subject.MinimumFirmware, subject.Feature, firmwareVersion, configuredFeatures);
    }

    /// <summary>
    /// Checks a firmware and feature requirement. <c>null</c> configured features means the controller does not report them, so feature gates pass.
    /// </summary>
    public static bool IsSupported(
        string? minimumFirmware, LuxtronikFeature feature, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
        => IsSupportedCore(minimumFirmware is null ? null : Version.Parse(minimumFirmware), feature, firmwareVersion, configuredFeatures);

    private static bool IsSupportedCore(
        Version? minimumFirmware, LuxtronikFeature feature, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        if (minimumFirmware is not null && firmwareVersion < minimumFirmware)
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
