using Namotion.Proxy.Abstractions;
using Namotion.Proxy.SampleBlazor.Components;
using Namotion.Proxy.SampleBlazor.Models;

using System.Reactive.Subjects;

namespace Namotion.Proxy.SampleBlazor
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var subject = new Subject<ProxyChangedContext>();
            var context = ProxyContext
                .CreateBuilder()
                .WithFullPropertyTracking()
                .WithPropertyChangeRecorder()
                .WithPropertyChangedCallback(subject.OnNext)
                .Build();

            // Add services to the container.
            builder.Services
                .AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddSingleton<IObservable<ProxyChangedContext>>(subject);
            builder.Services.AddSingleton<IProxyContext>(context);
            builder.Services.AddSingleton(new Game(context));
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
