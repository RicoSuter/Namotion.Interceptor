using HomeBlaze.AI;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.ClaudeCli;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Components;
using HomeBlaze.History.Blazor;
using HomeBlaze.History.InMemory.Blazor;
using HomeBlaze.History.Mcp;
using HomeBlaze.History.Sqlite.Blazor;
using HomeBlaze.Host;
using HomeBlaze.Samples;
using HomeBlaze.OpcUa;
using HomeBlaze.OpcUa.Blazor;
using HomeBlaze.Plugins;
using HomeBlaze.Services;
using HomeBlaze.Services.Diagnostics;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Blazor;
using HomeBlaze.Storage.Blazor.Files;
using Namotion.Devices.Gpio;
using Namotion.Devices.Gpio.HomeBlaze;
using Namotion.Devices.MyStrom;
using Namotion.Devices.MyStrom.HomeBlaze;
using Namotion.Devices.Nuki;
using Namotion.Devices.Nuki.HomeBlaze;
using Namotion.Services.Pushover;
using Namotion.Services.Pushover.HomeBlaze;
using Namotion.Devices.Shelly;
using Namotion.Devices.Shelly.HomeBlaze;
using Namotion.Devices.Sonos;
using Namotion.Devices.Sonos.HomeBlaze;
using Namotion.Devices.FritzBox;
using Namotion.Devices.FritzBox.HomeBlaze;
using Namotion.Devices.SunSpec;
using Namotion.Devices.SunSpec.HomeBlaze;
using Namotion.Devices.Wallbox;
using Namotion.Devices.Wallbox.HomeBlaze;
using Namotion.Devices.Ecowitt;
using Namotion.Devices.Ecowitt.HomeBlaze;
using Namotion.Devices.Luxtronik;
using Namotion.Devices.Luxtronik.HomeBlaze;
using Namotion.Devices.Philips.Hue;
using Namotion.Devices.Philips.Hue.HomeBlaze;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace HomeBlaze;

/// <summary>Builds the HomeBlaze web application.</summary>
/// <remarks>This is the single composition root. Both the command line entry point and the desktop
/// application call it, so neither can drift from the other's service registrations.</remarks>
public static class HomeBlazeWebApplication
{
    /// <summary>Builds and configures the application without starting it.</summary>
    public static async Task<WebApplication> CreateAsync(
        HomeBlazeWebApplicationOptions options,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [.. options.CommandLineArguments],
            ContentRootPath = options.ContentRootPath,
        });

        if (options.UseBuildStaticWebAssets)
        {
            builder.WebHost.UseStaticWebAssets();
        }

        var overrides = new Dictionary<string, string?>();
        if (options.RootConfigurationFile is not null)
        {
            overrides[HomeBlazePaths.RootConfigurationFileKey] = options.RootConfigurationFile;
        }

        if (options.SuppressSeeding)
        {
            overrides[HomeBlazePaths.SeedDirectoryKey] = string.Empty;
        }
        else if (options.SeedDirectory is not null)
        {
            overrides[HomeBlazePaths.SeedDirectoryKey] = options.SeedDirectory;
        }

        if (options.PluginConfigurationPath is not null)
        {
            overrides[HomeBlazePaths.PluginConfigurationPathKey] = options.PluginConfigurationPath;
        }

        if (overrides.Count > 0)
        {
            // Added last, so it wins over appsettings and environment variables.
            builder.Configuration.AddInMemoryCollection(overrides);
        }

        if (options.Urls.Count > 0)
        {
            builder.WebHost.UseUrls([.. options.Urls]);
        }

        if (options.LogFilePath is not null)
        {
            // A windowed host has no console, so the file is the only place its log can be read.
            builder.Logging.AddFile(options.LogFilePath, minimumLevel: LogLevel.Information);
        }

        builder.AddServiceDefaults();

        // Add all HomeBlaze services (cascades: Host -> Host.Services -> Services)
        // This registers the singleton IInterceptorSubjectContext with HostedServiceHandler
        builder.Services.AddHomeBlazeHost();
        builder.Services.AddHomeBlazeStorage();

        // Seeding must run before AddHomeBlazePlugins, which reads the plugin configuration during registration.
        var rootConfigurationPath = HomeBlazePaths.GetRootConfigurationPath(builder.Configuration);
        var seedDirectory = builder.Configuration[HomeBlazePaths.SeedDirectoryKey];
        var seededFileCount = DataDirectorySeeder.SeedIfMissing(rootConfigurationPath, seedDirectory);

        var pluginConfigPath = HomeBlazePaths.GetPluginConfigurationPath(builder.Configuration);

        builder.Services.AddHomeBlazePlugins(pluginConfigPath);
        builder.Services.AddHotKeys2();

        // The history package contributes its own tool; the AI package knows nothing about it. Registered once so
        // both the external MCP server and the built-in agents get it.
        builder.Services.AddHomeBlazeMcpToolProvider(context => new HistoryMcpToolProvider(
            context.RootSubjectProvider,
            context.PathProvider,
            context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<HistoryMcpToolProvider>()));
        builder.Services.AddHomeBlazeAgents();

        // The external MCP server at /mcp (default: off, enabled in Development). The built-in agents do not use it.
        var mcpEnabled = builder.Configuration.GetValue("McpServer:Enabled", false);
        if (mcpEnabled)
        {
            builder.Services.AddMcpServer()
                .WithHttpTransport(transportOptions => transportOptions.Stateless = true)
                .WithHomeBlazeMcpTools(isReadOnly: builder.Configuration.GetValue("McpServer:ReadOnly", true));
        }

        // Add services to the container.
        builder.Services
            .AddHttpClient();

        builder.Services
            .AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        if (seededFileCount > 0)
        {
            app.Logger.LogInformation("Seeded the data directory with {Count} default files.", seededFileCount);
        }

        // Replaced on every start, unlike the seeded files, so the documentation always matches the running version.
        // It must stay before app.Run, which starts the storage file watcher. Outdated documentation must not keep
        // the application down, so a failure is only logged.
        try
        {
            var replacedFileCount = DataDirectorySeeder.ReplaceDocumentation(rootConfigurationPath, seedDirectory);
            if (replacedFileCount > 0)
            {
                app.Logger.LogInformation("Replaced the documentation with {Count} files from the seed directory.", replacedFileCount);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            app.Logger.LogWarning(exception, "Could not replace the documentation from the seed directory. It may be outdated or incomplete until the next start.");
        }

        // Configure TypeProvider with application-specific assemblies
        // This must happen before any service that depends on TypeProvider is used
        var typeProvider = app.Services.GetRequiredService<TypeProvider>();
        typeProvider
            .AddAssembly(typeof(HostSystem).Assembly)                  // HomeBlaze.Services
            .AddAssembly(typeof(FluentStorageContainer).Assembly)      // HomeBlaze.Storage
            .AddAssembly(typeof(MarkdownFilePageComponent).Assembly)   // HomeBlaze.Storage.Blazor
            .AddAssembly(typeof(Widget).Assembly)                      // HomeBlaze.Components
            .AddAssembly(typeof(Motor).Assembly)                       // HomeBlaze.Samples
            .AddAssembly(typeof(OpcUaServer).Assembly)                 // HomeBlaze.OpcUa
            .AddAssembly(typeof(OpcUaServerEditComponent).Assembly)    // HomeBlaze.OpcUa.Blazor
            .AddAssembly(typeof(GpioSubject).Assembly)
            .AddAssembly(typeof(GpioSubjectEditComponent).Assembly)
            .AddAssembly(typeof(HueBridge).Assembly)
            .AddAssembly(typeof(HueBridgeSetupComponent).Assembly)
            .AddAssembly(typeof(MyStromSwitch).Assembly)
            .AddAssembly(typeof(MyStromSwitchWidget).Assembly)
            .AddAssembly(typeof(NukiBridge).Assembly)
            .AddAssembly(typeof(NukiBridgeWidget).Assembly)
            .AddAssembly(typeof(PushoverService).Assembly)
            .AddAssembly(typeof(PushoverServiceWidget).Assembly)
            .AddAssembly(typeof(ShellyDevice).Assembly)
            .AddAssembly(typeof(ShellyDeviceWidget).Assembly)
            .AddAssembly(typeof(SunSpecDevice).Assembly)
            .AddAssembly(typeof(SunSpecDeviceWidget).Assembly)
            .AddAssembly(typeof(WallboxCharger).Assembly)
            .AddAssembly(typeof(WallboxChargerWidget).Assembly)
            .AddAssembly(typeof(EcowittGateway).Assembly)
            .AddAssembly(typeof(EcowittGatewayWidget).Assembly)
            .AddAssembly(typeof(HomeBlaze.History.InMemory.InMemoryHistoryStoreSubject).Assembly)   // HomeBlaze.History.InMemory
            .AddAssembly(typeof(InMemoryHistoryStoreEditComponent).Assembly)                        // HomeBlaze.History.InMemory.Blazor
            .AddAssembly(typeof(HomeBlaze.History.Sqlite.SqliteHistoryStoreSubject).Assembly)       // HomeBlaze.History.Sqlite
            .AddAssembly(typeof(SqliteHistoryStoreEditComponent).Assembly)                          // HomeBlaze.History.Sqlite.Blazor
            .AddAssembly(typeof(HistoryChart).Assembly)                                             // HomeBlaze.History.Blazor
            .AddAssembly(typeof(HomeBlaze.Installation.HomeBlazeInstallation).Assembly)             // HomeBlaze.Installation
            .AddAssembly(typeof(HomeBlaze.Installation.Blazor.InstallationWidget).Assembly)         // HomeBlaze.Installation.Blazor
            .AddAssembly(typeof(LuxtronikHeatPump).Assembly)                                        // Namotion.Devices.Luxtronik
            .AddAssembly(typeof(LuxtronikHeatPumpWidget).Assembly)                                  // Namotion.Devices.Luxtronik.HomeBlaze
            .AddAssembly(typeof(SonosSystem).Assembly)                                              // Namotion.Devices.Sonos
            .AddAssembly(typeof(SonosSystemWidget).Assembly)                                        // Namotion.Devices.Sonos.HomeBlaze
            .AddAssembly(typeof(FritzBox).Assembly)                                                 // Namotion.Devices.FritzBox
            .AddAssembly(typeof(FritzBoxWidget).Assembly)                                           // Namotion.Devices.FritzBox.HomeBlaze
            .AddAssembly(typeof(ClaudeCliAgent).Assembly)                                           // HomeBlaze.AI
            .AddAssembly(typeof(HomeBlaze.AI.Blazor.AgentPage).Assembly);                           // HomeBlaze.AI.Blazor

        // Register HomeBlaze.Plugins subject types
        typeProvider.AddAssembly(typeof(PluginManager).Assembly);

        // Load runtime plugins
        var pluginLoader = app.Services.GetRequiredService<PluginLoader>();
        var pluginResult = await pluginLoader.LoadPluginsAsync(cancellationToken);
        if (pluginResult != null)
        {
            foreach (var plugin in pluginResult.LoadedPlugins)
            {
                foreach (var assembly in plugin.Assemblies)
                {
                    typeProvider.AddAssembly(assembly);
                }
            }
        }

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.UseAntiforgery();

        // Map the external MCP server endpoint if enabled
        if (mcpEnabled)
        {
            app.MapMcp("/mcp");
        }

        app.MapDefaultEndpoints();
        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}
