using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
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
            :: {{typeof(NestedSecretDeviceGroup).FullName}}#group
            ```json
            {
              "Primary": { "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "Host": "primary-host", "Password": "primary-secret" },
              "Gateways": { "north": { "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "Host": "north-host", "Password": "north-secret" } },
              "Backups": [ { "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "Host": "backup-host", "Password": "backup-secret" } ]
            }
            ```
            ::
            """;

        // Act
        var result = EmbeddedSubjectRedaction.Redact(
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = group });

        // Assert
        Assert.DoesNotContain("primary-secret", result);
        Assert.DoesNotContain("north-secret", result);
        Assert.DoesNotContain("backup-secret", result);
        Assert.Contains("primary-host", result);
        Assert.Contains("north-host", result);
        Assert.Contains("backup-host", result);
        Assert.Equal(3, result.Split("\"Password\": \"***\"").Length - 1);
    }

    [Fact]
    public void WhenAKeyDiffersInCaseOrADictionaryHasNumbersAsKeys_ThenTheSecretsAreRedacted()
    {
        // Arrange
        var context = SubjectContextFactory.Create(new ServiceCollection());
        var group = new NestedSecretDeviceGroup(context)
        {
            Primary = new EmbeddedSecretDevice { Host = "primary-host", Password = "primary-secret" },
            Numbered = new Dictionary<int, EmbeddedSecretDevice>
            {
                [7] = new() { Host = "numbered-host", Password = "numbered-secret" }
            }
        };

        var source = $$"""
            :: {{typeof(NestedSecretDeviceGroup).FullName}}#group
            ```json
            {
              "Primary": { "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "Host": "primary-host", "Password": "primary-secret", "password": "lower-secret" },
              "Numbered": { "7": { "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "Host": "numbered-host", "Password": "numbered-secret" } }
            }
            ```
            ::
            """;

        // Act
        var result = EmbeddedSubjectRedaction.Redact(
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = group });

        // Assert
        Assert.DoesNotContain("primary-secret", result);
        Assert.DoesNotContain("lower-secret", result);
        Assert.DoesNotContain("numbered-secret", result);
        Assert.Contains("numbered-host", result);
    }

    [Theory]
    [InlineData(":: HomeBlaze.Samples.Motor#motor Password=s3cr3t \"broken\n::", ":: HomeBlaze.Samples.Motor#motor ***\n::")]
    [InlineData(":: Password=s3cr3t\n::", ":: ***\n::")]
    [InlineData(":: HomeBlaze.Samples.Motor#bad=s3cr3t Name=x\n::", ":: HomeBlaze.Samples.Motor ***\n::")]
    public void WhenAHeadLineCannotBeRead_ThenEverythingAfterTheFenceAndTheNameIsRedacted(string source, string expected)
    {
        // Act
        var result = EmbeddedSubjectRedaction.Redact(MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject>());

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("- Item\n\n  :: HomeBlaze.Samples.Motor Password=s3cr3t\n  ```json\n  { \"Password\": \"json-secret\" }\n  ```\n  ::\n")]
    [InlineData("> :: HomeBlaze.Samples.Motor Password=s3cr3t\n> ```json\n> { \"Password\": \"json-secret\" }\n> ```\n> ::\n")]
    [InlineData("::: Footer Password=s3cr3t\n```json\n{ \"Password\": \"json-secret\" }\n```\n:::\n")]
    public void WhenAnInvalidBlockHasArgumentsAndAJsonBlock_ThenBothAreRedacted(string source)
    {
        // Act
        var result = EmbeddedSubjectRedaction.Redact(MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject>());

        // Assert
        Assert.DoesNotContain("s3cr3t", result);
        Assert.DoesNotContain("json-secret", result);
        Assert.Contains("Password=***", result);
    }

    [Fact]
    public void WhenThePageHasAnOldSubjectFence_ThenItsContentIsRedacted()
    {
        // Arrange
        const string source = "# Old\n\n```subject(gateway)\n{\n  \"$type\": \"Some.Device\",\n  \"password\": \"s3cr3t\"\n}\n```\n\nText";

        // Act
        var result = EmbeddedSubjectRedaction.Redact(MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject>());

        // Assert
        Assert.Equal("# Old\n\n```subject(gateway)\n***\n```\n\nText", result);
    }
}

[InterceptorSubject]
public partial class NestedSecretDeviceGroup : IConfigurable
{
    public NestedSecretDeviceGroup()
    {
        Gateways = new Dictionary<string, EmbeddedSecretDevice>();
        Numbered = new Dictionary<int, EmbeddedSecretDevice>();
        Backups = [];
    }

    [Configuration]
    public partial EmbeddedSecretDevice? Primary { get; set; }

    [Configuration]
    public partial Dictionary<string, EmbeddedSecretDevice> Gateways { get; set; }

    [Configuration]
    public partial Dictionary<int, EmbeddedSecretDevice> Numbered { get; set; }

    [Configuration]
    public partial EmbeddedSecretDevice[] Backups { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
