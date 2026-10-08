using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Coffee.Tests;

public class CoffeeMachineSimulatorTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private static CoffeeMachine CreateMachine()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        return new CoffeeMachine(context);
    }

    private static void Run(CoffeeMachineSimulator simulator, TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Tick)
        {
            simulator.Step(Tick);
        }
    }

    [Fact]
    public void WhenStartingCold_ThenBoilerHeatsAndMachineBecomesReady()
    {
        // Arrange
        var machine = CreateMachine();
        var simulator = new CoffeeMachineSimulator(machine);

        // Act
        simulator.Step(Tick);
        var stateWhileCold = machine.State;
        Run(simulator, TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(CoffeeMachineState.Heating, stateWhileCold);
        Assert.Equal(CoffeeMachineState.Idle, machine.State);
        Assert.True(machine.IsReady);
        Assert.InRange(machine.Boiler.Temperature, 91.5, 94);
    }

    [Fact]
    public void WhenBrewingEspresso_ThenCupIsBrewedAndSuppliesAreConsumed()
    {
        // Arrange
        var machine = CreateMachine();
        var simulator = new CoffeeMachineSimulator(machine);
        Run(simulator, TimeSpan.FromSeconds(30));

        // Act
        machine.Brew("Espresso");
        Run(simulator, TimeSpan.FromSeconds(2));
        var pressureWhileBrewing = machine.Pump.Pressure;
        Run(simulator, TimeSpan.FromSeconds(20));

        // Assert
        Assert.InRange(pressureWhileBrewing, 8, 9);
        Assert.Equal(1, machine.CupsBrewed);
        Assert.Equal(CoffeeMachineState.Idle, machine.State);
        Assert.False(machine.Pump.IsRunning);
        Assert.Null(machine.ActiveRecipeName);
        Assert.InRange(machine.WaterTank.Level, 97, 97.5);
        Assert.Equal(100 - CoffeeMachineSimulator.BeansPerCup, machine.BeanHopper.Level);
    }

    [Fact]
    public void WhenSeedIsTheSame_ThenTemperaturesAreIdentical()
    {
        // Arrange
        var first = CreateMachine();
        var second = CreateMachine();

        // Act
        Run(new CoffeeMachineSimulator(first, seed: 7), TimeSpan.FromSeconds(10));
        Run(new CoffeeMachineSimulator(second, seed: 7), TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(first.Boiler.Temperature, second.Boiler.Temperature);
    }

    [Fact]
    public void WhenBrewingWithoutActiveRecipe_ThenStepDoesNotThrowAndDrawsNoWater()
    {
        // Arrange
        var machine = CreateMachine();
        machine.State = CoffeeMachineState.Brewing;
        machine.Pump.IsRunning = true;
        var simulator = new CoffeeMachineSimulator(machine);

        // Act
        Run(simulator, TimeSpan.FromSeconds(3));

        // Assert
        Assert.Equal(100, machine.WaterTank.Level);
    }

    [Fact]
    public void WhenEventIsScheduled_ThenItIsAppliedOnceAtItsTime()
    {
        // Arrange
        var machine = CreateMachine();
        var applied = 0;
        var events = new[]
        {
            new SimulatorEvent(TimeSpan.FromSeconds(1), m => { applied++; m.WaterTank.Level = 5; })
        };
        var simulator = new CoffeeMachineSimulator(machine, events: events);

        // Act
        Run(simulator, TimeSpan.FromSeconds(0.9));
        var levelBefore = machine.WaterTank.Level;
        Run(simulator, TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(100, levelBefore);
        Assert.Equal(5, machine.WaterTank.Level);
        Assert.Equal(1, applied);
    }
}
