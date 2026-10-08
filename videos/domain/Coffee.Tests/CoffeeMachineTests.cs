using System.ComponentModel.DataAnnotations;
using System.Reactive.Concurrency;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit;

namespace Coffee.Tests;

public class CoffeeMachineTests
{
    private static CoffeeMachine CreateMachine(out IInterceptorSubjectContext context)
    {
        context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithDataAnnotationValidation();

        return new CoffeeMachine(context);
    }

    [Fact]
    public void WhenBoilerIsHotAndTankIsFull_ThenMachineIsReady()
    {
        // Arrange
        var machine = CreateMachine(out _);

        // Act
        machine.Boiler.Temperature = 93;

        // Assert
        Assert.True(machine.IsReady);
        Assert.Equal("Ready", machine.Status);
    }

    [Fact]
    public void WhenBoilerIsCold_ThenStatusShowsHeating()
    {
        // Arrange
        var machine = CreateMachine(out _);

        // Act
        machine.Boiler.Temperature = 78.4;

        // Assert
        Assert.False(machine.IsReady);
        Assert.Equal("Heating 78 °C", machine.Status);
    }

    [Fact]
    public void WhenWaterTankIsLow_ThenStatusAsksForRefill()
    {
        // Arrange
        var machine = CreateMachine(out _);
        machine.Boiler.Temperature = 93;

        // Act
        machine.WaterTank.Level = 5;

        // Assert
        Assert.False(machine.IsReady);
        Assert.Equal("Refill water", machine.Status);
    }

    [Fact]
    public void WhenBrewingEspresso_ThenStatePumpAndTargetAreSet()
    {
        // Arrange
        var machine = CreateMachine(out _);
        machine.Boiler.Temperature = 93;

        // Act
        machine.Brew("Espresso");

        // Assert
        Assert.Equal(CoffeeMachineState.Brewing, machine.State);
        Assert.Equal("Espresso", machine.ActiveRecipeName);
        Assert.True(machine.Pump.IsRunning);
        Assert.Equal(93, machine.Boiler.TargetTemperature);
        Assert.Equal("Brewing Espresso", machine.Status);
    }

    [Fact]
    public void WhenMachineIsNotReady_ThenBrewThrows()
    {
        // Arrange
        var machine = CreateMachine(out _);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => machine.Brew("Espresso"));
    }

    [Fact]
    public void WhenTargetTemperatureIsOutOfRange_ThenWriteIsRejected()
    {
        // Arrange
        var machine = CreateMachine(out _);

        // Act & Assert
        Assert.Throws<ValidationException>(() => machine.Boiler.TargetTemperature = 120);
        Assert.Equal(93, machine.Boiler.TargetTemperature);
    }

    [Fact]
    public void WhenBoilerHeatsUp_ThenIsReadyChangeIsPublished()
    {
        // Arrange
        var machine = CreateMachine(out var context);
        var changes = new List<string>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change.Property.Name));

        // Act
        machine.Boiler.Temperature = 93;

        // Assert
        Assert.Contains(nameof(CoffeeMachine.IsReady), changes);
        Assert.Contains(nameof(CoffeeMachine.Status), changes);
    }
}
