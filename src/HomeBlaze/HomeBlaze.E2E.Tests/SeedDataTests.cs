using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

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
        // The build output's Seed folder is what the image ships as /app/Seed, including the linked docs.
        var seedDirectory = Path.Combine(AppContext.BaseDirectory, "Seed");
        var typeProvider = _fixture.ServerServices.GetRequiredService<TypeProvider>();
        var seedFiles = Directory.GetFiles(seedDirectory, "*.json", SearchOption.AllDirectories);

        // Act
        var checkedFiles = new List<string>();
        var failures = new List<string>();
        foreach (var seedFile in seedFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(seedFile));
            foreach (var typeName in CollectTypeDiscriminators(document.RootElement).Distinct())
            {
                checkedFiles.Add(Path.GetRelativePath(seedDirectory, seedFile));

                // The loader only creates non-abstract configurable types; any other type leaves a plain JSON file.
                var isLoadable = typeProvider.Types.Any(type =>
                    type.FullName == typeName &&
                    typeof(IConfigurable).IsAssignableFrom(type) &&
                    type is { IsAbstract: false, IsInterface: false });
                if (!isLoadable)
                {
                    failures.Add($"{seedFile}: '{typeName}' is not a configurable type of the app");
                }
            }
        }

        // Assert
        Assert.Contains("Root.json", checkedFiles);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
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
