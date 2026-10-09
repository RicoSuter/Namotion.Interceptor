using Coffee;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

var builder = WebApplication.CreateBuilder(args);

// Short, path-free console output so a terminal capture of the startup reads well on screen.
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithDataAnnotationValidation();

builder.Services.AddSingleton(new CoffeeMachine(context));
builder.Services.AddHostedService<CoffeeMachineSimulatorService>();

var app = builder.Build();

app.MapGet("/", () => Results.Content(StatusPage.Html, "text/html"));
app.MapGet("/controls", () => Results.Content(ControlsPage.Html, "text/html"));

app.MapGet("/status", (CoffeeMachine machine) => new
{
    machine.Status,
    machine.IsReady,
    machine.Boiler.Temperature,
    machine.Boiler.TargetTemperature,
    machine.CupsBrewed
});

app.MapPost("/brew/{recipe}", (CoffeeMachine machine, string recipe) =>
{
    if (!machine.IsReady || !machine.Recipes.ContainsKey(recipe))
    {
        return Results.Conflict();
    }

    machine.Brew(recipe);
    return Results.Accepted();
});

var machine = app.Services.GetRequiredService<CoffeeMachine>();
app.Logger.LogInformation("Simulating {Name}, boiler heats to {Target} °C", machine.Name, machine.Boiler.TargetTemperature);

app.Run();
