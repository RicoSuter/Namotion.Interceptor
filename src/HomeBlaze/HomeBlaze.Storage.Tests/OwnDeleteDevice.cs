using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A configurable subject with a delete operation of its own, which a storage that holds it keeps.
/// </summary>
[InterceptorSubject]
public partial class OwnDeleteDevice : IConfigurable
{
    [Configuration]
    public partial string Name { get; set; }

    public OwnDeleteDevice()
    {
        Name = string.Empty;
    }

    [Operation(Title = "Unpair", Purpose = OperationPurpose.Delete)]
    public void Unpair()
    {
    }

    public static string Serialize(string name)
        => $$"""
            {
              "$type": "{{typeof(OwnDeleteDevice).FullName}}",
              "name": "{{name}}"
            }
            """;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
