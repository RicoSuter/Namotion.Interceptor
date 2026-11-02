using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A configurable subject whose constructor takes a service, as a device that gets an HTTP client does. The generator
/// mirrors that constructor with a trailing context parameter, so dependency injection creates the device anchored
/// provisionally to the context of the service provider.
/// </summary>
[InterceptorSubject]
public partial class InjectedDevice : IConfigurable
{
    [Configuration]
    public partial string Name { get; set; }

    public partial double? Power { get; set; }

    public InjectedDevice(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        Name = string.Empty;
    }

    public static string Serialize(string name)
        => $$"""
            {
              "$type": "{{typeof(InjectedDevice).FullName}}",
              "name": "{{name}}"
            }
            """;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The same device without a constructor that takes the context: the generator never mirrors an obsolete
/// constructor, so dependency injection creates the device unattached and it gets its context only from the folder
/// that holds it.
/// </summary>
[InterceptorSubject]
public partial class UnanchoredInjectedDevice : IConfigurable
{
    [Configuration]
    public partial string Name { get; set; }

    public partial double? Power { get; set; }

    [Obsolete("Suppresses the generated constructor that takes the context.")]
    public UnanchoredInjectedDevice(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        Name = string.Empty;
    }

    public static string Serialize(string name)
        => $$"""
            {
              "$type": "{{typeof(UnanchoredInjectedDevice).FullName}}",
              "name": "{{name}}"
            }
            """;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
