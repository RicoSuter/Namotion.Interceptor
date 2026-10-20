using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class MetadataSearchTests
{
    private static (MetadataPathsTestFolder Root, MetadataSearch Search) CreateTree()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var device = new MetadataPathsTestDevice(context) { Channels = [new MetadataPathsTestDevice()] };
        var root = new MetadataPathsTestFolder(context) { Children = new Dictionary<string, IInterceptorSubject> { ["Pump"] = device } };
        TestMetadata.Set(device, """{ "tags": ["floor-2", "Pumps"], "area": "Basement" }""");
        TestMetadata.Set(device.Channels[0], """{ "tags": ["floor-1"] }""");
        var values = TestMetadata.Parse("""{ "notes": "Calibrated 2026", "tags": ["Floor-2"] }""");
        device.TryGetRegisteredSubject()!.TryGetProperty(nameof(MetadataPathsTestDevice.Channels))!
            .AddAttribute(KnownAttributes.Metadata, values.GetType(), _ => values, null);

        return (root, new MetadataSearch(new SubjectPathResolver(() => root)));
    }

    [Theory]
    [InlineData("floor-2", null, null, null, new[] { "/Pump", "/Pump/Channels" })]
    [InlineData(null, "notes", null, null, new[] { "/Pump/Channels" })]
    [InlineData(null, "area", "basement", null, new[] { "/Pump" })]
    [InlineData(null, null, "FLOOR-1", null, new[] { "/Pump/Channels[0]" })]
    [InlineData("floor-1", null, null, "/Pump/Channels[0]", new[] { "/Pump/Channels[0]" })]
    [InlineData("floor-2", null, null, "/Other", new string[0])]
    public void WhenMetadataIsSearched_ThenTheSubjectsAndPropertiesThatMatchEveryFilterAreFound(
        string? tag, string? key, string? value, string? path, string[] expectedPaths)
    {
        // Arrange
        var (root, search) = CreateTree();

        // Act
        var paths = search.Search(root, tag, key, value, path);

        // Assert
        Assert.Equal(expectedPaths.Order(StringComparer.Ordinal), paths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WhenTheTagsAreRead_ThenEachTagIsListedOnce()
    {
        // Arrange
        var (root, search) = CreateTree();

        // Act
        var tags = search.GetTags(root);

        // Assert
        Assert.Equal(["floor-1", "floor-2", "Pumps"], tags);
    }
}
