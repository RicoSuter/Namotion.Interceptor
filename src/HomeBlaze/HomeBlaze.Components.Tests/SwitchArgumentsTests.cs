using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Controls;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SwitchArgumentsTests
{
    private static MethodMetadata CreateOperation(params MethodParameter[] parameters) =>
        new(_ => null) { Kind = MethodKind.Operation, Title = "Operation", Parameters = parameters };

    [Fact]
    public void WhenNeitherOnNorOffIsSet_ThenThereIsNoError()
    {
        // Act
        var error = SwitchArguments.Validate(null, null, null, null);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenOnAndOffAreOperationsWithoutInput_ThenThereIsNoError()
    {
        // Arrange
        var on = CreateOperation();
        var off = CreateOperation();

        // Act
        var error = SwitchArguments.Validate("pump/PowerOn", "pump/PowerOff", on, off);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("pump/PowerOn", null)]
    [InlineData(null, "pump/PowerOff")]
    [InlineData("pump/PowerOn", "")]
    public void WhenOnlyOneOfOnAndOffIsSet_ThenItIsAnError(string? on, string? off)
    {
        // Act
        var error = SwitchArguments.Validate(on, off, null, null);

        // Assert
        Assert.Equal("Set both On and Off, or neither", error);
    }

    [Fact]
    public void WhenOnNeedsInput_ThenItIsAnErrorThatNamesThePath()
    {
        // Arrange
        var on = CreateOperation(new MethodParameter { Name = "speed", Type = typeof(int) });
        var off = CreateOperation();

        // Act
        var error = SwitchArguments.Validate("pump/SetTargetSpeed", "pump/PowerOff", on, off);

        // Assert
        Assert.Equal("On needs an operation without input: pump/SetTargetSpeed", error);
    }

    [Fact]
    public void WhenOffNeedsInput_ThenItIsAnErrorThatNamesThePath()
    {
        // Arrange
        var on = CreateOperation();
        var off = CreateOperation(new MethodParameter { Name = "speed", Type = typeof(int) });

        // Act
        var error = SwitchArguments.Validate("pump/PowerOn", "pump/SetTargetSpeed", on, off);

        // Assert
        Assert.Equal("Off needs an operation without input: pump/SetTargetSpeed", error);
    }

    [Fact]
    public void WhenTheOnlyParameterIsProvidedByTheRuntime_ThenThereIsNoError()
    {
        // Arrange
        var on = CreateOperation(new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true });
        var off = CreateOperation();

        // Act
        var error = SwitchArguments.Validate("pump/PowerOn", "pump/PowerOff", on, off);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenOnAndOffLeadNowhere_ThenThereIsNoError()
    {
        // Act
        var error = SwitchArguments.Validate("pump/Missing", "pump/Gone", null, null);

        // Assert
        Assert.Null(error);
    }
}
