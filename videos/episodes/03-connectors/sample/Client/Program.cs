using Coffee;
using Microsoft.Extensions.Logging.Console;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.WebSocket;

var builder = WebApplication.CreateBuilder(args);

// Short, path-free console output so a terminal capture reads well on screen.
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
builder.Logging.AddConsoleFormatter<ShortConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddConsole(options => options.FormatterName = ShortConsoleFormatter.FormatterName);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

#region ClientSetup
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle();

builder.Services.AddSingleton(new CoffeeMachine(context));

builder.Services.AddWebSocketSubjectClientSource<CoffeeMachine>(configuration =>
{
    configuration.ServerUri = new Uri("ws://localhost:5310/ws");
});
#endregion

var app = builder.Build();

app.MapGet("/", () => Results.Content(StatusPage.Render("Client", "#64d2ff", canBrew: true), "text/html"));

#region SourceState
app.MapGet("/status", (CoffeeMachine machine) =>
{
    var temperature = new PropertyReference(
        machine.Boiler, nameof(Boiler.Temperature));
    var state = temperature.TryGetSource(out var source)
        ? source.State
        : SourceState.Unclaimed;

    return StatusPage.Status(machine, state.ToString());
});
#endregion

#region BrewEndpoint
app.MapPost("/brew/{recipe}", (CoffeeMachine machine, string recipe) =>
{
    machine.Brew(recipe);
    return Results.Accepted();
});
#endregion

app.Run();
