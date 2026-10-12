using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

public class EmbeddedSubjectRedactionTests
{
    [Fact]
    public void WhenSecretsAreInANestedSubjectADictionaryAndAnArray_ThenAllAreRedacted()
    {
        // Arrange
        var context = SubjectContextFactory.Create(new ServiceCollection());
        var group = new NestedSecretDeviceGroup(context)
        {
            Primary = new EmbeddedSecretDevice { Host = "primary-host", Password = "primary-secret" },
            Gateways = new Dictionary<string, EmbeddedSecretDevice>
            {
                ["north"] = new() { Host = "north-host", Password = "north-secret" }
            },
            Backups = [new EmbeddedSecretDevice { Host = "backup-host", Password = "backup-secret" }]
        };

        var source = $$"""
            ```subject(group)
            {
              "$type": "{{typeof(NestedSecretDeviceGroup).FullName}}",
              "primary": { "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "host": "primary-host", "password": "primary-secret" },
              "gateways": { "north": { "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "host": "north-host", "password": "north-secret" } },
              "backups": [ { "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "host": "backup-host", "password": "backup-secret" } ]
            }
            ```
            """;

        // Act
        var result = EmbeddedSubjectRedaction.Redact(source, new Dictionary<string, IInterceptorSubject> { ["group"] = group });

        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain("primary-secret", result);
        Assert.DoesNotContain("north-secret", result);
        Assert.DoesNotContain("backup-secret", result);
        Assert.Contains("primary-host", result);
        Assert.Contains("north-host", result);
        Assert.Contains("backup-host", result);
        Assert.Equal(3, result.Split("\"password\": \"***\"").Length - 1);
    }
}

[InterceptorSubject]
public partial class NestedSecretDeviceGroup : IConfigurable
{
    public NestedSecretDeviceGroup()
    {
        Gateways = new Dictionary<string, EmbeddedSecretDevice>();
        Backups = [];
    }

    [Configuration]
    public partial EmbeddedSecretDevice? Primary { get; set; }

    [Configuration]
    public partial Dictionary<string, EmbeddedSecretDevice> Gateways { get; set; }

    [Configuration]
    public partial EmbeddedSecretDevice[] Backups { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
