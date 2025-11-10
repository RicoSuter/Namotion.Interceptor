using MudBlazor.Services;
using HomeBlaze.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;

using Host = HomeBlaze.Client.State.Host;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();

// Add Namotion.Interceptor.Blazor services
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithReadPropertyRecorder();

builder.Services
    .AddSingleton<Host>(_ => new Host(context))
    .AddHostedService(sp => sp.GetRequiredService<Host>());

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
    // .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    // .AddInteractiveWebAssemblyRenderMode()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(HomeBlaze.Client._Imports).Assembly);

app.Run();