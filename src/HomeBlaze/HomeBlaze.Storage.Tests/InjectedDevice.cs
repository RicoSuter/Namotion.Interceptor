using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A configurable subject whose constructor takes a service, as a device that gets an HTTP client does. The generator
/// adds no constructor that takes the context, so the device gets its services only from the folder that holds it.
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
