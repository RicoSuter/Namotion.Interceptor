using Microsoft.Extensions.Hosting;

namespace Coffee;

public sealed class CoffeeMachineSimulatorService(CoffeeMachine machine) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var simulator = new CoffeeMachineSimulator(machine);
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            simulator.Step(TickInterval);
        }
    }
}
