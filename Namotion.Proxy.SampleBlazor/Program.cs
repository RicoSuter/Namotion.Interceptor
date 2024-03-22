using Namotion.Proxy.Abstractions;
using Namotion.Proxy.SampleBlazor.Components;
using System.Reactive.Subjects;

namespace Namotion.Proxy.SampleBlazor
{
    [GenerateProxy]
    public abstract class GameBase
    {
        public virtual Player[] Players { get; protected set; } = [];

        public void AddPlayer(Player player)
        {
            lock (this)
                Players = [..Players, player];
        }

        public void RemovePlayer(Player player)
        {
            lock (this)
                Players = Players.Where(p => p != player).ToArray();
        }
    }

    [GenerateProxy]
    public class PlayerBase : IDisposable
    {
        private readonly Game _game;

        public virtual string Name { get; set; } = Guid.NewGuid().ToString();

        public PlayerBase(Game game)
        {
            _game = game;
            _game.AddPlayer((Player)this);
        }

        public void Dispose()
        {
            _game.RemovePlayer((Player)this);
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var subject = new Subject<ProxyChanged>();
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

            builder.Services.AddSingleton<IObservable<ProxyChanged>>(subject);
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
