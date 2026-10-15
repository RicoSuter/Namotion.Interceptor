using System.Text.Json;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// Every "$type" in the seeded configuration files resolves to a type of the running app; a moved or renamed
/// subject type would otherwise silently vanish from new installations.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SeedDataTests
{
    private readonly PlaywrightFixture _fixture;

    public SeedDataTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void WhenSeedFilesAreRead_ThenEveryTypeDiscriminatorResolves()
    {
        // Arrange
        var seedDirectory = GetSeedDirectory();
        var typeProvider = _fixture.ServerServices.GetRequiredService<TypeProvider>();
        var seedFiles = Directory.EnumerateFiles(seedDirectory, "*.json", SearchOption.AllDirectories);

        // Act
        var failures = new List<string>();
        foreach (var seedFile in seedFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(seedFile));
            foreach (var typeName in CollectTypeDiscriminators(document.RootElement).Distinct())
            {
                // Mirrors the resolution ConfigurableSubjectSerializer.Deserialize performs for a "$type" value.
                if (typeProvider.Types.All(type => type.FullName != typeName))
                {
                    failures.Add($"{seedFile}: '{typeName}' does not resolve to a known type");
                }
            }
        }

        // Assert
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Resolved like Program.cs: the configured seed directory, otherwise the Seed folder next to the app.
    private string GetSeedDirectory()
    {
        var configuration = _fixture.ServerServices.GetRequiredService<IConfiguration>();
        var configuredSeedDirectory = configuration[HomeBlazePaths.SeedDirectoryKey];
        var environment = _fixture.ServerServices.GetRequiredService<IHostEnvironment>();
        var seedDirectory = string.IsNullOrWhiteSpace(configuredSeedDirectory)
            ? Path.Combine(environment.ContentRootPath, "Seed")
            : Path.GetFullPath(configuredSeedDirectory);

        if (!Directory.Exists(seedDirectory))
        {
            throw new DirectoryNotFoundException($"The seed directory '{seedDirectory}' does not exist.");
        }

        return seedDirectory;
    }

    // A configurable subject can hold another one with its own "$type", so all depths are searched.
    private static IEnumerable<string> CollectTypeDiscriminators(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() is { Length: > 0 } typeName)
                    {
                        yield return typeName;
                    }

                    foreach (var nested in CollectTypeDiscriminators(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in CollectTypeDiscriminators(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
