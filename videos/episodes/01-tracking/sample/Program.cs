using System.ComponentModel.DataAnnotations;
using Coffee;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

// Short, path-free console output so a terminal capture reads well on screen.
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
builder.Logging.AddConsoleFormatter<ShortConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddConsole(options => options.FormatterName = ShortConsoleFormatter.FormatterName);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

builder.Services.AddSingleton<MachineHosts>();
foreach (var id in MachineHosts.Ids)
{
    // One simulator per machine; AddHostedService would register the same service type only once.
    builder.Services.AddSingleton<IHostedService>(serviceProvider =>
        new CoffeeMachineSimulatorService(serviceProvider.GetRequiredService<MachineHosts>().Get(id).Machine));
}

var app = builder.Build();

// Every route below starts with the machine id, "brew" or "brew-async"; other ids are not found.
var machines = app.MapGroup("/{machine}").AddEndpointFilter(async (context, next) =>
{
    var hosts = context.HttpContext.RequestServices.GetRequiredService<MachineHosts>();
    return context.HttpContext.Request.RouteValues["machine"] is string id && hosts.Contains(id)
        ? await next(context)
        : Results.NotFound();
});

machines.MapGet("/", (string machine, MachineHosts hosts) =>
    Results.Content(Pages.Machine(hosts.Get(machine)), "text/html"));

machines.MapGet("/changes", (string machine, MachineHosts hosts) =>
    Results.Content(Pages.Stream(hosts.Get(machine)), "text/html"));

machines.MapGet("/status", (string machine, MachineHosts hosts) => Pages.Status(hosts.Get(machine)));

machines.MapGet("/stream", (string machine, MachineHosts hosts) => hosts.Get(machine).Stream.Snapshot());

machines.MapPost("/brew/{recipe}", async (string machine, string recipe, MachineHosts hosts) =>
{
    try
    {
        await hosts.Get(machine).BrewAsync(recipe);
        return Results.Accepted();
    }
    catch (Exception exception) when (exception is InvalidOperationException or ValidationException or KeyNotFoundException)
    {
        return Results.BadRequest(exception.Message.Trim());
    }
});

#region AddRecipe
machines.MapPost("/recipes", (string machine, MachineHosts hosts) =>
{
    var coffeeMachine = hosts.Get(machine).Machine;
    var ristretto = new Recipe
    {
        Name = "Ristretto", WaterAmount = 25, Temperature = 97
    };
    coffeeMachine.Recipes = new Dictionary<string, Recipe>(coffeeMachine.Recipes)
    {
        ["Ristretto"] = ristretto
    };
    return Results.Accepted();
});
#endregion

// Demo helper: a cold, idle machine with its original recipes and an empty change stream, so every recording
// starts from the same state.
machines.MapPost("/reset", (string machine, MachineHosts hosts) =>
{
    var host = hosts.Get(machine);
    var coffeeMachine = host.Machine;
    coffeeMachine.Pump.IsRunning = false;
    coffeeMachine.ActiveRecipeName = null;
    coffeeMachine.State = CoffeeMachineState.Idle;
    coffeeMachine.Boiler.TargetTemperature = 93;
    coffeeMachine.Boiler.Temperature = 20;
    coffeeMachine.CupsBrewed = 0;
    coffeeMachine.WaterTank.Level = 100;
    coffeeMachine.BeanHopper.Level = 100;
    coffeeMachine.Recipes = coffeeMachine.Recipes
        .Where(recipe => recipe.Key != "Ristretto")
        .ToDictionary(recipe => recipe.Key, recipe => recipe.Value);
    host.Stream.Clear();
    return Results.Accepted();
});

// Demo helper: empties the change stream without touching the machine.
machines.MapPost("/clear", (string machine, MachineHosts hosts) =>
{
    hosts.Get(machine).Stream.Clear();
    return Results.Accepted();
});

app.Logger.LogInformation("Heating two machines to 93 °C, one brews with Brew and one with BrewAsync");

app.Run();
