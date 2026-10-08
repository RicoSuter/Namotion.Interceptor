using Coffee;
using Microsoft.Extensions.Logging.Console;
using Connectors.Server.Grinder;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.WebSocket;
using Namotion.Interceptor.WebSocket.Server;

var builder = WebApplication.CreateBuilder(args);

// Short, path-free console output so a terminal capture reads well on screen.
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
builder.Logging.AddConsoleFormatter<ShortConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddConsole(options => options.FormatterName = ShortConsoleFormatter.FormatterName);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

#region ServerSetup
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle();

builder.Services.AddSingleton(new CoffeeMachine(context));
builder.Services.AddHostedService<CoffeeMachineSimulatorService>();

builder.Services.AddWebSocketSubjectHandler<CoffeeMachine>("/ws");
#endregion

builder.Services.AddSingleton<SimulatedGrinderDevice>();
builder.Services.AddSingleton<IGrinderDevice>(serviceProvider => serviceProvider.GetRequiredService<SimulatedGrinderDevice>());
builder.Services.AddGrinderSource(serviceProvider => serviceProvider.GetRequiredService<CoffeeMachine>().BeanHopper);

var app = builder.Build();
var page = StatusPage.Render("Server", "#bf5af2", canBrew: false);

#region MapHandler
app.UseWebSockets();
app.MapWebSocketSubjectHandler("/ws");

app.MapGet("/", () => Results.Content(page, "text/html"));
#endregion

app.MapGet("/status", (CoffeeMachine machine, [FromKeyedServices("/ws")] WebSocketSubjectHandler handler) =>
    StatusPage.Status(machine, handler.ConnectionCount == 1 ? "1 client" : $"{handler.ConnectionCount} clients"));

// Demo helper: a cold machine with full tanks and no cups yet, so every recording starts from the same state.
app.MapPost("/reset", (CoffeeMachine machine) =>
{
    machine.Boiler.Temperature = 20;
    machine.CupsBrewed = 0;
    machine.WaterTank.Level = 100;
    machine.BeanHopper.Level = 100;
    return Results.Accepted();
});

app.Run();
