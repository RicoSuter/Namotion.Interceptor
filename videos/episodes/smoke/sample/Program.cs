using Coffee;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

var builder = WebApplication.CreateBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithDataAnnotationValidation();

builder.Services.AddSingleton(new CoffeeMachine(context));
builder.Services.AddHostedService<CoffeeMachineSimulatorService>();

var app = builder.Build();

app.MapGet("/status", (CoffeeMachine machine) => new
{
    machine.Status,
    machine.IsReady,
    machine.Boiler.Temperature
});

app.MapPost("/brew/{recipe}", (CoffeeMachine machine, string recipe) =>
{
    machine.Brew(recipe);
    return Results.Accepted();
});

app.Run();
