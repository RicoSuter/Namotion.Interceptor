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
    private static readonly HashSet<MarkdownBlock> NoLayoutBlocks = [];

    [Fact]
    public void WhenTheSubjectOfABlockIsNoLongerRegistered_ThenEveryArgumentAndJsonValueIsRedacted()
    {
        // Arrange: the subject that a reload detached, which tells no configuration property anymore.
        var device = new EmbeddedSecretDevice { Host = "detached-host", Password = "detached-secret" };
        var source = $$"""
            :: {{typeof(EmbeddedSecretDevice).FullName}}#gateway Host=detached-host Password=detached-secret
            ```json
            { "Host": "json-host", "Password": "json-secret" }
            ```
            ::
            """;

        // Act
        var result = EmbeddedSubjectRedaction.Redact(
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = device }, NoLayoutBlocks, out _);

        // Assert
        Assert.DoesNotContain("detached-secret", result);
        Assert.DoesNotContain("json-secret", result);
        Assert.DoesNotContain("detached-host", result);
        Assert.DoesNotContain("json-host", result);
        Assert.Contains("#gateway Host=*** Password=***\n", result);
    }

    [Fact]
    public void WhenAnArgumentIsNoConfigurationPropertyOfTheSubject_ThenItIsRedactedAndTheOthersAreShown()
    {
        // Arrange
        var context = SubjectContextFactory.Create(new ServiceCollection());
        var device = new EmbeddedSecretDevice(context) { Host = "visible-host", Password = "s3cr3t" };
        var source = $":: {typeof(EmbeddedSecretDevice).FullName}#gateway Host=visible-host Password=s3cr3t Token=unknown\n::";

        // Act
        var result = EmbeddedSubjectRedaction.Redact(
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = device }, NoLayoutBlocks, out _);

        // Assert
        Assert.Equal($":: {typeof(EmbeddedSecretDevice).FullName}#gateway Host=visible-host Password=*** Token=***\n::", result);
    }

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
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = group }, NoLayoutBlocks, out _);

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
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = group }, NoLayoutBlocks, out _);

        // Assert
        Assert.DoesNotContain("primary-secret", result);
        Assert.DoesNotContain("lower-secret", result);
        Assert.DoesNotContain("numbered-secret", result);
        Assert.Contains("numbered-host", result);
    }

    [Fact]
    public void WhenNestedJsonDoesNotMatchTheValuesOfTheSubject_ThenItIsHidden()
    {
        // Arrange: no primary at runtime, a dictionary entry and a list item without a counterpart, a list item of
        // another type and a key that is no configuration property.
        var context = SubjectContextFactory.Create(new ServiceCollection());
        var group = new NestedSecretDeviceGroup(context)
        {
            Gateways = new Dictionary<string, EmbeddedSecretDevice> { ["north"] = new() { Host = "north-host" } },
            Backups = [new EmbeddedSecretDevice { Host = "backup-host" }]
        };

        var device = typeof(EmbeddedSecretDevice).FullName;
        var source = $$"""
            :: {{typeof(NestedSecretDeviceGroup).FullName}}#group
            ```json
            {
              "Primary": { "$type": "{{device}}", "Password": "primary-secret" },
              "Gateways": {
                "north": { "$type": "{{device}}", "Host": "north-host", "Token": "token-secret" },
                "south": { "$type": "{{device}}", "Password": "south-secret" }
              },
              "Backups": [
                { "$type": "Other.Device", "Password": "other-type-secret" },
                { "$type": "{{device}}", "Password": "extra-item-secret" }
              ]
            }
            ```
            ::
            """;

        // Act
        var result = EmbeddedSubjectRedaction.Redact(
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = group }, NoLayoutBlocks, out _);

        // Assert
        Assert.DoesNotContain("primary-secret", result);
        Assert.DoesNotContain("token-secret", result);
        Assert.DoesNotContain("south-secret", result);
        Assert.DoesNotContain("other-type-secret", result);
        Assert.DoesNotContain("extra-item-secret", result);
        Assert.Contains("north-host", result);
    }

    [Fact]
    public void WhenAPlainObjectHasKeysThatAreNoPropertiesOfIt_ThenTheyAreHidden()
    {
        // Arrange
        var context = SubjectContextFactory.Create(new ServiceCollection());
        var host = new PlainOptionsHost(context) { Options = new PlainOptions { Endpoint = "https://example.com" } };
        var source = $$"""
            :: {{typeof(PlainOptionsHost).FullName}}#host
            ```json
            {
              "Options": { "Endpoint": "https://example.com", "ApiKey": "api-secret", "Nested": { "Token": "nested-secret" } },
              "Missing": { "Value": "missing-secret" }
            }
            ```
            ::
            """;

        // Act
        var result = EmbeddedSubjectRedaction.Redact(
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = host }, NoLayoutBlocks, out _);

        // Assert
        Assert.DoesNotContain("api-secret", result);
        Assert.DoesNotContain("nested-secret", result);
        Assert.DoesNotContain("missing-secret", result);
        Assert.Contains("https://example.com", result);
    }

    [Fact]
    public void WhenAPlainObjectHasASecretProperty_ThenItsValueIsHidden()
    {
        // Arrange
        var context = SubjectContextFactory.Create(new ServiceCollection());
        var host = new PlainOptionsHost(context) { Credentials = new PlainCredentials { User = "admin", Password = "plain-secret" } };
        var source = $$"""
            :: {{typeof(PlainOptionsHost).FullName}}#host
            ```json
            {
              "Credentials": { "User": "admin", "password": "plain-secret" }
            }
            ```
            ::
            """;

        // Act
        var result = EmbeddedSubjectRedaction.Redact(
            MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject> { [0] = host }, NoLayoutBlocks, out _);

        // Assert
        Assert.DoesNotContain("plain-secret", result);
        Assert.Contains("\"password\": \"***\"", result);
        Assert.Contains("\"User\": \"admin\"", result);
    }

    [Fact]
    public void WhenABlockIsInsideAnInvalidBlock_ThenItsArgumentsAndItsJsonBlockAreRedacted()
    {
        // Arrange
        const string source = """
            :: HomeBlaze.Components.Card "broken
            ::: Namotion.Devices.Shelly.ShellyDevice#s Password=hunter2
            ```json
            { "Password": "json-hunter2" }
            ```
            :::
            ::
            """;

        // Act
        var result = EmbeddedSubjectRedaction.Redact(MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject>(), NoLayoutBlocks, out _);

        // Assert
        Assert.Equal("""
            :: HomeBlaze.Components.Card ***
            ::: Namotion.Devices.Shelly.ShellyDevice#s Password=***
            ```json
            ***
            ```
            :::
            ::
            """, result);
    }

    [Theory]
    [InlineData(":: HomeBlaze.Samples.Motor#motor Password=s3cr3t \"broken\n::", ":: HomeBlaze.Samples.Motor#motor ***\n::")]
    [InlineData(":: Password=s3cr3t\n::", ":: Password ***\n::")]
    [InlineData(":: HomeBlaze.Samples.Motor#bad=s3cr3t Name=x\n::", ":: HomeBlaze.Samples.Motor ***\n::")]
    public void WhenAHeadLineCannotBeRead_ThenEverythingAfterTheFenceAndTheNameIsRedacted(string source, string expected)
    {
        // Act
        var result = EmbeddedSubjectRedaction.Redact(MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject>(), NoLayoutBlocks, out _);

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
        var result = EmbeddedSubjectRedaction.Redact(MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject>(), NoLayoutBlocks, out _);

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
        var result = EmbeddedSubjectRedaction.Redact(MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject>(), NoLayoutBlocks, out _);

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

[InterceptorSubject]
public partial class PlainOptionsHost : IConfigurable
{
    [Configuration]
    public partial PlainOptions? Options { get; set; }

    [Configuration]
    public partial PlainCredentials? Credentials { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PlainOptions
{
    public string? Endpoint { get; set; }
}

public struct PlainCredentials
{
    public string? User { get; set; }

    [Configuration(IsSecret = true)]
    public string? Password { get; set; }
}
