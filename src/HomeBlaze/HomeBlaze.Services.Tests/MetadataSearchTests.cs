using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class MetadataSearchTests
{
    private static MetadataPathsTestFolder CreateTree()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var device = new MetadataPathsTestDevice(context) { Channels = [new MetadataPathsTestDevice()] };
        var root = new MetadataPathsTestFolder(context) { Children = new Dictionary<string, IInterceptorSubject> { ["Pump"] = device } };
        TestMetadata.Set(root, """{ "tags": ["Root"], "floor": 2, "active": true }""");
        TestMetadata.Set(device, """{ "tags": ["floor-2", "Pumps"], "area": "Basement" }""");
        TestMetadata.Set(device.Channels[0], """{ "tags": ["floor-1"] }""");
        var values = TestMetadata.Parse("""{ "notes": "Calibrated 2026", "tags": ["Floor-2", "channels-only"] }""");
        device.TryGetRegisteredSubject()!.TryGetProperty(nameof(MetadataPathsTestDevice.Channels))!
            .AddAttribute(KnownAttributes.Metadata, values.GetType(), _ => values, null);

        return root;
    }

    [Theory]
    [InlineData("floor-2", null, null, null, new[] { "Pump|", "Pump|Channels" })]
    [InlineData(null, "notes", null, null, new[] { "Pump|Channels" })]
    [InlineData(null, "AREA", null, null, new string[0])]
    [InlineData(null, "area", "basement", null, new[] { "Pump|" })]
    [InlineData(null, "area", "Base", null, new string[0])]
    [InlineData(null, null, "FLOOR-1", null, new[] { "Pump/Channels[0]|" })]
    [InlineData(null, "floor", "2", null, new string[0])]
    [InlineData(null, "active", "true", null, new string[0])]
    [InlineData("floor-1", null, null, "/Pump/Channels[0]", new[] { "Pump/Channels[0]|" })]
    [InlineData("floor-2", null, null, "Pump", new[] { "Pump|", "Pump|Channels" })]
    [InlineData("floor-2", null, null, "/Other", new string[0])]
    public void WhenMetadataIsSearched_ThenTheSubjectsAndPropertiesThatMatchEveryFilterAreFound(
        string? tag, string? key, string? value, string? path, string[] expectedMatches)
    {
        // Arrange
        var root = CreateTree();

        // Act
        var matches = MetadataSearch.Search(root, tag, key, value, path);

        // Assert
        Assert.Equal(expectedMatches, matches.Select(match => $"{match.Path}|{match.Property}"));
    }

    [Fact]
    public void WhenEverythingWithMetadataIsSearched_ThenTheMatchesAreOrderedByPathAndThenProperty()
    {
        // Arrange
        var root = CreateTree();

        // Act
        var matches = MetadataSearch.Search(root, key: "tags");

        // Assert
        Assert.Equal(
            [new MetadataSearchMatch("", null), new MetadataSearchMatch("Pump", null), new MetadataSearchMatch("Pump", "Channels"), new MetadataSearchMatch("Pump/Channels[0]", null)],
            matches);
    }

    [Fact]
    public void WhenTheTagsAreRead_ThenEachTagIsListedOnce()
    {
        // Arrange
        var root = CreateTree();

        // Act
        var tags = MetadataSearch.GetTags(root);

        // Assert
        Assert.Equal(["channels-only", "floor-1", "floor-2", "Pumps", "Root"], tags);
    }

    [Fact]
    public void WhenASubjectIsExcluded_ThenNoMatchAndNoTagIsFoundInOrBelowIt()
    {
        // Arrange
        var root = CreateTree();
        var device = root.Children["Pump"];
        bool IsExcluded(IInterceptorSubject subject) => ReferenceEquals(subject, device);

        // Act
        var matches = MetadataSearch.Search(root, key: "tags", isExcluded: IsExcluded);
        var tags = MetadataSearch.GetTags(root, IsExcluded);

        // Assert
        Assert.Equal([new MetadataSearchMatch("", null)], matches);
        Assert.Equal(["Root"], tags);
    }

    [Fact]
    public void WhenAPropertyIsNotIncluded_ThenTheTagsOnlyOnItAreNotListed()
    {
        // Arrange
        var root = CreateTree();

        // Act
        var tags = MetadataSearch.GetTags(root, isPropertyIncluded: property => property.Name != nameof(MetadataPathsTestDevice.Channels));

        // Assert
        Assert.Equal(["floor-1", "floor-2", "Pumps", "Root"], tags);
    }

    [Fact]
    public void WhenATagWasEditedByHandToABlankOne_ThenItIsNotListed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new MetadataPathsTestFolder(context);
        TestMetadata.Set(root, """{ "tags": ["", " ", "pump"] }""");

        // Act
        var tags = MetadataSearch.GetTags(root);

        // Assert
        Assert.Equal(["pump"], tags);
    }
}
