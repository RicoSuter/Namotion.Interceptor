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
        var isSupported = LuxtronikGating.IsSupported(minimumFirmwareVersion, LuxtronikFunction.None, new Version(major, minor, patch), functionMask: null);

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Fact]
    public void WhenFunctionIsNotConfigured_ThenItIsNotSupported()
    {
        // Arrange
        var functionMask = LuxtronikFunctionMask.Of(LuxtronikFunction.Heating);

        // Act
        var isSupported = LuxtronikGating.IsSupported(null, LuxtronikFunction.Pool, new Version(3, 92, 3), functionMask);

        // Assert
        Assert.False(isSupported);
    }

    [Fact]
    public void WhenFunctionFlagsAreUnknown_ThenFunctionGatesAreIgnored()
    {
        // Act
        var isSupported = LuxtronikGating.IsSupported(null, LuxtronikFunction.Pool, new Version(3, 92, 3), functionMask: null);

        // Assert
        Assert.True(isSupported);
    }

    [Fact]
    public void WhenReadingFunctionFlags_ThenSetBitsBecomeActiveFunctions()
    {
        // Arrange
        var flags = new bool[12];
        flags[0] = true;
        flags[2] = true;
        flags[6] = true;

        // Act
        var functionMask = LuxtronikGating.GetFunctionMask(flags);

        // Assert
        Assert.Equal(
            new[] { LuxtronikFunction.Heating, LuxtronikFunction.Cooling, LuxtronikFunction.MixingCircuit1Heating },
            Enum.GetValues<LuxtronikFunction>().Where(function => LuxtronikGating.IsActive(functionMask, function)));
    }

    [Fact]
    public void WhenFunctionMaskIsUnknown_ThenEveryFunctionIsActive()
    {
        // Act
        var isActive = Enum.GetValues<LuxtronikFunction>().All(function => LuxtronikGating.IsActive(null, function));

        // Assert
        Assert.True(isActive);
    }

    [Fact]
    public void WhenFunctionFlagsDiffer_ThenMaskDifferenceNamesTheChangedFunctions()
    {
        // Arrange
        var previousFlags = new bool[12];
        previousFlags[0] = true;
        previousFlags[7] = true;
        var currentFlags = new bool[12];
        currentFlags[0] = true;
        currentFlags[2] = true;

        // Act
        var changedMask = LuxtronikGating.GetFunctionMask(previousFlags) ^ LuxtronikGating.GetFunctionMask(currentFlags);

        // Assert
        Assert.Equal("Cooling, MixingCircuit1Cooling", LuxtronikGating.GetFunctionNames(changedMask));
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
        var isSupported = LuxtronikGating.IsSupported(property, Version.Parse(firmware), functionMask: null);

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Theory]
    [InlineData(LuxtronikFunction.None, nameof(LuxtronikGatedTestSubject.FunctionGated), new[] { LuxtronikFunction.Cooling }, true)]
    [InlineData(LuxtronikFunction.None, nameof(LuxtronikGatedTestSubject.FunctionGated), new[] { LuxtronikFunction.Heating }, false)]
    [InlineData(LuxtronikFunction.Pool, nameof(LuxtronikGatedTestSubject.Ungated), new[] { LuxtronikFunction.Heating }, false)]
    [InlineData(LuxtronikFunction.Pool, nameof(LuxtronikGatedTestSubject.FunctionGated), new[] { LuxtronikFunction.Cooling }, false)]
    [InlineData(LuxtronikFunction.Pool, nameof(LuxtronikGatedTestSubject.FunctionGated), new[] { LuxtronikFunction.Cooling, LuxtronikFunction.Pool }, true)]
    public void WhenCheckingPropertyFunction_ThenPropertyAndSubjectFunctionsMustBothBeActive(
        LuxtronikFunction subjectFunction, string propertyName, LuxtronikFunction[] activeFunctions, bool expected)
    {
        // Arrange
        var property = GetProperty(new LuxtronikGatedTestSubject(CreateContext()) { SubjectFunction = subjectFunction }, propertyName);

        // Act
        var isSupported = LuxtronikGating.IsSupported(property, new Version(3, 92, 3), LuxtronikFunctionMask.Of(activeFunctions));

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Theory]
    [InlineData(0, new[] { LuxtronikFunction.MixingCircuit1Heating }, true)]
    [InlineData(2, new[] { LuxtronikFunction.MixingCircuit2Heating }, true)]
    [InlineData(2, new[] { LuxtronikFunction.MixingCircuit1Heating }, false)]
    [InlineData(4, new[] { LuxtronikFunction.MixingCircuit3Heating }, true)]
    [InlineData(4, new[] { LuxtronikFunction.MixingCircuit3Cooling }, false)]
    public void WhenCircuitSubjectShiftsItsFunctions_ThenTheRegisterRequiresTheCircuitsOwnFlag(
        int functionOffset, LuxtronikFunction[] activeFunctions, bool expected)
    {
        // Arrange
        var property = GetProperty(
            new LuxtronikCircuitTestSubject(CreateContext()) { FunctionOffset = functionOffset },
            nameof(LuxtronikCircuitTestSubject.CircuitGated));

        // Act
        var isSupported = LuxtronikGating.IsSupported(property, new Version(3, 92, 3), LuxtronikFunctionMask.Of(activeFunctions));

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Fact]
    public void WhenCircuitSubjectShiftsItsFunctions_ThenAnUngatedRegisterStaysUngated()
    {
        // Arrange
        var property = GetProperty(
            new LuxtronikCircuitTestSubject(CreateContext()) { FunctionOffset = 2 },
            nameof(LuxtronikCircuitTestSubject.Ungated));

        // Act
        var isSupported = LuxtronikGating.IsSupported(
            property, new Version(3, 92, 3), LuxtronikFunctionMask.Of(LuxtronikFunction.Heating));

        // Assert
        Assert.True(isSupported);
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
            .OfType<ILuxtronikGate>()
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
