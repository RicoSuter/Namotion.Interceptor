namespace Coffee;

/// <summary>
/// Deterministic physics for the coffee machine; call <see cref="Step"/> with the elapsed time.
/// </summary>
public sealed class CoffeeMachineSimulator
{
    public const double HeatingRate = 4.0;
    public const double CoolingRate = 0.3;
    public const double BrewCoolingRate = 1.5;
    public const double PressureRate = 6.0;
    public const double MaximumPressure = 9.0;
    public const double FlowPressure = 8.0;
    public const double FlowRate = 4.0;
    public const double BeansPerCup = 1.5;

    private readonly CoffeeMachine _machine;
    private readonly Random _random;
    private readonly Queue<SimulatorEvent> _events;
    private TimeSpan _elapsed;
    private double _brewedWater;

    public CoffeeMachineSimulator(CoffeeMachine machine, int seed = 42, IEnumerable<SimulatorEvent>? events = null)
    {
        _machine = machine;
        _random = new Random(seed);
        _events = new Queue<SimulatorEvent>((events ?? []).OrderBy(e => e.At));
    }

    public void Step(TimeSpan delta)
    {
        _elapsed += delta;
        while (_events.Count > 0 && _events.Peek().At <= _elapsed)
        {
            _events.Dequeue().Apply(_machine);
        }

        var seconds = delta.TotalSeconds;
        UpdateBoiler(seconds);
        UpdatePump(seconds);
        UpdateBrew(seconds);
        UpdateState();
    }

    private void UpdateBoiler(double seconds)
    {
        var boiler = _machine.Boiler;
        boiler.HeaterOn = boiler.Temperature < boiler.TargetTemperature - 0.5;

        var rate = boiler.HeaterOn ? HeatingRate : -CoolingRate;
        if (_machine.State == CoffeeMachineState.Brewing)
        {
            rate -= BrewCoolingRate;
        }

        var noise = (_random.NextDouble() - 0.5) * 0.04;
        boiler.Temperature = Math.Round(boiler.Temperature + rate * seconds + noise, 2);
    }

    private void UpdatePump(double seconds)
    {
        var pump = _machine.Pump;
        var pressure = pump.IsRunning
            ? Math.Min(MaximumPressure, pump.Pressure + PressureRate * seconds)
            : Math.Max(0, pump.Pressure - PressureRate * seconds);
        pump.Pressure = Math.Round(pressure, 2);
    }

    private void UpdateBrew(double seconds)
    {
        if (_machine.State != CoffeeMachineState.Brewing || _machine.Pump.Pressure < FlowPressure)
        {
            return;
        }

        var water = FlowRate * seconds;
        _brewedWater += water;
        _machine.WaterTank.Level -= water / WaterTank.CapacityInMilliliters * 100;

        var recipe = _machine.Recipes[_machine.ActiveRecipeName!];
        if (_brewedWater >= recipe.WaterAmount)
        {
            _brewedWater = 0;
            _machine.Pump.IsRunning = false;
            _machine.BeanHopper.Level -= BeansPerCup;
            _machine.CupsBrewed++;
            _machine.ActiveRecipeName = null;
            _machine.State = CoffeeMachineState.Idle;
        }
    }

    private void UpdateState()
    {
        if (_machine.State == CoffeeMachineState.Idle && !_machine.Boiler.IsHot)
        {
            _machine.State = CoffeeMachineState.Heating;
        }
        else if (_machine.State == CoffeeMachineState.Heating && _machine.Boiler.IsHot)
        {
            _machine.State = CoffeeMachineState.Idle;
        }
    }
}
