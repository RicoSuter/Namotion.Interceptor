using System.ComponentModel.DataAnnotations;
using System.Reactive.Concurrency;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;
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

    private static CoffeeMachine CreateMachineWithTransactions()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithDataAnnotationValidation()
            .WithTransactions();

        return new CoffeeMachine(context);
    }

    /// <summary>Adds a recipe created outside the context, whose temperature the boiler rejects.</summary>
    private static void AddRecipeAboveBoilerRange(CoffeeMachine machine)
    {
        machine.Recipes = new Dictionary<string, Recipe>(machine.Recipes)
        {
            ["Ristretto"] = new() { Name = "Ristretto", WaterAmount = 25, Temperature = 97 }
        };
    }

    [Fact]
    public void WhenBoilerIsHotAndTankIsFull_ThenMachineIsReady()
    {
        // Arrange
        var machine = CreateMachine(out _);

        // Act
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 93.0);

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
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 78.4);

        // Assert
        Assert.False(machine.IsReady);
        Assert.Equal("Heating 78 °C", machine.Status);
    }

    [Fact]
    public void WhenWaterTankIsLow_ThenStatusAsksForRefill()
    {
        // Arrange
        var machine = CreateMachine(out _);
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 93.0);

        // Act
        machine.WaterTank.SetFromSource(nameof(WaterTank.Level), 5.0);

        // Assert
        Assert.False(machine.IsReady);
        Assert.Equal("Refill water", machine.Status);
    }

    [Fact]
    public void WhenLowWaterTankIsRefilled_ThenMachineIsReady()
    {
        // Arrange
        var machine = CreateMachine(out _);
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 93.0);
        machine.WaterTank.SetFromSource(nameof(WaterTank.Level), 5.0);

        // Act
        machine.WaterTank.Refill();

        // Assert
        Assert.Equal(100, machine.WaterTank.Level);
        Assert.True(machine.IsReady);
        Assert.Equal("Ready", machine.Status);
    }

    [Fact]
    public void WhenBeanHopperIsRefilled_ThenLevelIsFull()
    {
        // Arrange
        var machine = CreateMachine(out _);
        machine.BeanHopper.SetFromSource(nameof(BeanHopper.Level), 20.0);

        // Act
        machine.BeanHopper.Refill();

        // Assert
        Assert.Equal(100, machine.BeanHopper.Level);
    }

    [Fact]
    public void WhenBrewingEspresso_ThenStatePumpAndTargetAreSet()
    {
        // Arrange
        var machine = CreateMachine(out _);
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 93.0);

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
    public void WhenBrewWriteIsRejected_ThenEarlierWritesStayApplied()
    {
        // Arrange
        var machine = CreateMachine(out _);
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 93.0);
        AddRecipeAboveBoilerRange(machine);

        // Act & Assert
        Assert.Throws<ValidationException>(() => machine.Brew("Ristretto"));
        Assert.Equal(CoffeeMachineState.Brewing, machine.State);
        Assert.Equal("Ristretto", machine.ActiveRecipeName);
        Assert.False(machine.Pump.IsRunning);
        Assert.False(machine.IsReady);
    }

    [Fact]
    public async Task WhenBrewingLungoAsync_ThenStatePumpAndTargetAreSet()
    {
        // Arrange
        var machine = CreateMachineWithTransactions();
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 93.0);

        // Act
        await machine.BrewAsync("Lungo");

        // Assert
        Assert.Equal(CoffeeMachineState.Brewing, machine.State);
        Assert.Equal("Lungo", machine.ActiveRecipeName);
        Assert.True(machine.Pump.IsRunning);
        Assert.Equal(92, machine.Boiler.TargetTemperature);
        Assert.Equal("Brewing Lungo", machine.Status);
    }

    [Fact]
    public async Task WhenBrewAsyncWriteIsRejected_ThenNothingIsAppliedOrPublished()
    {
        // Arrange
        var machine = CreateMachineWithTransactions();
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 93.0);
        AddRecipeAboveBoilerRange(machine);
        var changes = new List<string>();
        using var subscription = ((IInterceptorSubject)machine).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change.Property.Name));

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() => machine.BrewAsync("Ristretto"));
        Assert.Equal(CoffeeMachineState.Idle, machine.State);
        Assert.Null(machine.ActiveRecipeName);
        Assert.False(machine.Pump.IsRunning);
        Assert.True(machine.IsReady);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task WhenMachineIsNotReady_ThenBrewAsyncThrows()
    {
        // Arrange
        var machine = CreateMachineWithTransactions();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => machine.BrewAsync("Espresso"));
        Assert.Equal(CoffeeMachineState.Idle, machine.State);
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
        machine.Boiler.SetFromSource(nameof(Boiler.Temperature), 93.0);

        // Assert
        Assert.Contains(nameof(CoffeeMachine.IsReady), changes);
        Assert.Contains(nameof(CoffeeMachine.Status), changes);
    }
}
