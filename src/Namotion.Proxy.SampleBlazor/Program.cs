using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Namotion.Proxy.SampleBlazor.Components;
using Namotion.Proxy.SampleBlazor.Models;

namespace Namotion.Proxy.SampleBlazor
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var collection = InterceptorCollection
                .Create()
                .WithFullPropertyTracking()
                .WithReadPropertyRecorder();

            // Add services to the container.
            builder.Services
                .AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddSingleton(collection.GetPropertyChangedObservable());          
            builder.Services.AddSingleton(collection.GetService<ReadPropertyRecorder>());          
            builder.Services.AddSingleton(new Game(collection));
            builder.Services.AddScoped<Player>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
