using System.Reflection;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikGatingTests
{
    [Theory]
    [InlineData(null, 3, 90, 1, true)]
    [InlineData("3.92.0", 3, 90, 1, false)]
    [InlineData("3.92.0", 3, 92, 0, true)]
    [InlineData("3.92.1", 3, 92, 0, false)]
    [InlineData("3.92.0", 3, 92, 3, true)]
    public void WhenCheckingFirmware_ThenMinimumVersionIsEnforced(string? minimumFirmware, int major, int minor, int patch, bool expected)
    {
        // Arrange
        var minimumFirmwareVersion = minimumFirmware is null ? null : Version.Parse(minimumFirmware);

        // Act
        var isSupported = LuxtronikGating.IsSupported(minimumFirmwareVersion, LuxtronikFeature.None, LuxtronikFeature.None, new Version(major, minor, patch), configuredFeatures: null);

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Fact]
    public void WhenFeatureIsNotConfigured_ThenItIsNotSupported()
    {
        // Arrange
        var configuredFeatures = new HashSet<LuxtronikFeature> { LuxtronikFeature.Heating };

        // Act
        var isSupported = LuxtronikGating.IsSupported(null, LuxtronikFeature.Pool, LuxtronikFeature.None, new Version(3, 92, 3), configuredFeatures);

        // Assert
        Assert.False(isSupported);
    }

    [Theory]
    [InlineData(new[] { LuxtronikFeature.MixingCircuit1Heating }, true)]
    [InlineData(new[] { LuxtronikFeature.MixingCircuit1Cooling }, true)]
    [InlineData(new[] { LuxtronikFeature.MixingCircuit1Heating, LuxtronikFeature.MixingCircuit1Cooling }, true)]
    [InlineData(new[] { LuxtronikFeature.Heating, LuxtronikFeature.MixingCircuit2Cooling }, false)]
    public void WhenFeatureHasAnAlternative_ThenEitherConfiguredFeatureSatisfiesIt(LuxtronikFeature[] configuredFeatures, bool expected)
    {
        // Act
        var isSupported = LuxtronikGating.IsSupported(
            null, LuxtronikFeature.MixingCircuit1Heating, LuxtronikFeature.MixingCircuit1Cooling, new Version(3, 92, 3), configuredFeatures.ToHashSet());

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Fact]
    public void WhenOnlyTheAlternativeFeatureIsConfiguredButFirmwareIsTooOld_ThenItIsNotSupported()
    {
        // Arrange
        var configuredFeatures = new HashSet<LuxtronikFeature> { LuxtronikFeature.MixingCircuit1Cooling };

        // Act
        var isSupported = LuxtronikGating.IsSupported(
            new Version(3, 92, 0), LuxtronikFeature.MixingCircuit1Heating, LuxtronikFeature.MixingCircuit1Cooling, new Version(3, 90, 1), configuredFeatures);

        // Assert
        Assert.False(isSupported);
    }

    [Fact]
    public void WhenFeatureFlagsAreUnknown_ThenFeatureGatesAreIgnored()
    {
        // Act
        var isSupported = LuxtronikGating.IsSupported(null, LuxtronikFeature.Pool, LuxtronikFeature.None, new Version(3, 92, 3), configuredFeatures: null);

        // Assert
        Assert.True(isSupported);
    }

    [Fact]
    public void WhenReadingFeatureFlags_ThenSetBitsBecomeConfiguredFeatures()
    {
        // Arrange
        var flags = new bool[12];
        flags[0] = true;
        flags[2] = true;
        flags[6] = true;

        // Act
        var features = LuxtronikGating.GetConfiguredFeatures(flags);

        // Assert
        Assert.Equal(
            new[] { LuxtronikFeature.Heating, LuxtronikFeature.Cooling, LuxtronikFeature.MixingCircuit1Heating },
            features.OrderBy(feature => feature));
    }

    [Fact]
    public void WhenFeatureFlagsDiffer_ThenMaskDifferenceNamesTheChangedFeatures()
    {
        // Arrange
        var previousFlags = new bool[12];
        previousFlags[0] = true;
        previousFlags[7] = true;
        var currentFlags = new bool[12];
        currentFlags[0] = true;
        currentFlags[2] = true;

        // Act
        var changedMask = LuxtronikGating.GetFeatureMask(previousFlags) ^ LuxtronikGating.GetFeatureMask(currentFlags);

        // Assert
        Assert.Equal("Cooling, MixingCircuit1Cooling", LuxtronikGating.GetFeatureNames(changedMask));
    }

    [Theory]
    [InlineData(null, nameof(LuxtronikGatedTestSubject.Ungated), "3.90.1", true)]
    [InlineData(null, nameof(LuxtronikGatedTestSubject.FirmwareGated), "3.92.3", false)]
    [InlineData(null, nameof(LuxtronikGatedTestSubject.FirmwareGated), "3.93.0", true)]
    [InlineData("3.92.0", nameof(LuxtronikGatedTestSubject.Ungated), "3.90.1", false)]
    [InlineData("3.92.0", nameof(LuxtronikGatedTestSubject.Ungated), "3.92.0", true)]
    [InlineData("3.94.0", nameof(LuxtronikGatedTestSubject.FirmwareGated), "3.93.0", false)]
    [InlineData("3.92.0", nameof(LuxtronikGatedTestSubject.FirmwareGated), "3.93.0", true)]
    public void WhenCheckingPropertyFirmware_ThenPropertyAndSubjectGatesMustBothPass(
        string? subjectMinimumFirmware, string propertyName, string firmware, bool expected)
    {
        // Arrange
        var property = GetProperty(new LuxtronikGatedTestSubject(CreateContext())
        {
            SubjectMinimumFirmwareVersion = subjectMinimumFirmware is null ? null : Version.Parse(subjectMinimumFirmware)
        }, propertyName);

        // Act
        var isSupported = LuxtronikGating.IsSupported(property, Version.Parse(firmware), configuredFeatures: null);

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Theory]
    [InlineData(LuxtronikFeature.None, nameof(LuxtronikGatedTestSubject.FeatureGated), new[] { LuxtronikFeature.Cooling }, true)]
    [InlineData(LuxtronikFeature.None, nameof(LuxtronikGatedTestSubject.FeatureGated), new[] { LuxtronikFeature.Heating }, false)]
    [InlineData(LuxtronikFeature.Pool, nameof(LuxtronikGatedTestSubject.Ungated), new[] { LuxtronikFeature.Heating }, false)]
    [InlineData(LuxtronikFeature.Pool, nameof(LuxtronikGatedTestSubject.FeatureGated), new[] { LuxtronikFeature.Cooling }, false)]
    [InlineData(LuxtronikFeature.Pool, nameof(LuxtronikGatedTestSubject.FeatureGated), new[] { LuxtronikFeature.Cooling, LuxtronikFeature.Pool }, true)]
    public void WhenCheckingPropertyFeature_ThenPropertyAndSubjectFeaturesMustBothBeConfigured(
        LuxtronikFeature subjectFeature, string propertyName, LuxtronikFeature[] configuredFeatures, bool expected)
    {
        // Arrange
        var property = GetProperty(new LuxtronikGatedTestSubject(CreateContext()) { SubjectFeature = subjectFeature }, propertyName);

        // Act
        var isSupported = LuxtronikGating.IsSupported(property, new Version(3, 92, 3), configuredFeatures.ToHashSet());

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Theory]
    [InlineData(new[] { LuxtronikFeature.Pool }, true)]
    [InlineData(new[] { LuxtronikFeature.Solar }, true)]
    [InlineData(new[] { LuxtronikFeature.Heating }, false)]
    public void WhenSubjectHasAnAlternativeFeature_ThenEitherOneLetsItsPropertiesBeRead(LuxtronikFeature[] configuredFeatures, bool expected)
    {
        // Arrange
        var property = GetProperty(new LuxtronikGatedTestSubject(CreateContext())
        {
            SubjectFeature = LuxtronikFeature.Pool,
            SubjectAlternativeFeature = LuxtronikFeature.Solar
        }, nameof(LuxtronikGatedTestSubject.Ungated));

        // Act
        var isSupported = LuxtronikGating.IsSupported(property, new Version(3, 92, 3), configuredFeatures.ToHashSet());

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Fact]
    public void WhenInspectingTheModel_ThenEveryRegisterMinimumFirmwareIsParseable()
    {
        // Arrange
        var registerAttributes = typeof(LuxtronikRegisterAttribute).Assembly.GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .SelectMany(property => property.GetCustomAttributes<LuxtronikRegisterAttribute>())
            .ToList();

        // Act
        var invalidFirmwares = registerAttributes
            .Where(attribute => attribute.MinimumFirmware is not null && !Version.TryParse(attribute.MinimumFirmware, out _))
            .Select(attribute => attribute.MinimumFirmware)
            .ToList();

        // Assert
        Assert.NotEmpty(registerAttributes);
        Assert.Empty(invalidFirmwares);
    }

    [Fact]
    public void WhenConstructingEveryModelSubject_ThenSubjectFirmwareGatesParse()
    {
        // Arrange
        var subjectTypes = typeof(LuxtronikRegisterAttribute).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<InterceptorSubjectAttribute>() is not null && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToList();

        // Act
        var gatedSubjects = subjectTypes
            .Select(type => Activator.CreateInstance(type)!)
            .SelectMany(subject => subject.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => property.GetValue(subject))
                .Prepend(subject))
            .OfType<ILuxtronikGatedSubject>()
            .ToList();

        // Assert
        Assert.NotEmpty(gatedSubjects);
        Assert.Contains(gatedSubjects, subject => subject.MinimumFirmwareVersion is not null);
    }

    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithRegistry();

    private static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string propertyName)
        => subject.TryGetRegisteredSubject()?.TryGetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property '{propertyName}' is not registered.");
}
