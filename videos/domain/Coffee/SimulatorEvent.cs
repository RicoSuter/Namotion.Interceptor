namespace Coffee;

public sealed record SimulatorEvent(TimeSpan At, Action<CoffeeMachine> Apply);
