using HomeBlaze.Abstractions;
using HomeBlaze.Services.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests.Metadata;

public class MetadataSearchTests
{
    private static MetadataSearchTestFolder CreateTree()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var device = new MetadataSearchTestDevice(context) { Channels = [new MetadataSearchTestDevice()] };
        var root = new MetadataSearchTestFolder(context) { Children = new Dictionary<string, IInterceptorSubject> { ["Pump"] = device } };
        TestMetadata.Set(root, """{ "tags": ["Root"], "floor": 2, "active": true }""");
        TestMetadata.Set(device, """{ "tags": ["floor-2", "Pumps"], "area": "Basement" }""");
        TestMetadata.Set(device.Channels[0], """{ "tags": ["floor-1"] }""");
        var values = TestMetadata.Parse("""{ "notes": "Calibrated 2026", "tags": ["Floor-2", "channels-only"] }""");
        device.TryGetRegisteredSubject()!.TryGetProperty(nameof(MetadataSearchTestDevice.Channels))!
            .AddAttribute(KnownAttributes.Metadata, values.GetType(), _ => values, null);

        return root;
    }

    [Theory]
    [InlineData("floor-2", null, null, new[] { "Pump|", "Pump|Channels" })]
    [InlineData(null, "notes", null, new[] { "Pump|Channels" })]
    [InlineData(null, "AREA", null, new string[0])]
    [InlineData(null, "area", "basement", new[] { "Pump|" })]
    [InlineData(null, "area", "Base", new string[0])]
    [InlineData(null, null, "FLOOR-1", new[] { "Pump/Channels[0]|" })]
    [InlineData(null, "floor", "2", new string[0])]
    [InlineData(null, "active", "true", new string[0])]
    public void WhenMetadataIsSearched_ThenTheSubjectsAndPropertiesThatMatchEveryFilterAreFound(
        string? tag, string? key, string? value, string[] expectedMatches)
    {
        // Arrange
        var root = CreateTree();

        // Act
        var matches = MetadataSearch.Search(root, tag, key, value);

        // Assert
        Assert.Equal(expectedMatches, matches.Select(match => $"{match.Path}|{match.Property}"));
    }

    [Fact]
    public void WhenTheSearchStartsAtASubject_ThenItAndTheSubjectsBelowItAreFoundByTheirPathsRelativeToTheRoot()
    {
        // Arrange
        var root = CreateTree();
        var device = root.Children["Pump"];

        // Act
        var matches = MetadataSearch.Search(root, key: "tags", start: device);

        // Assert
        Assert.Equal(["Pump|", "Pump|Channels", "Pump/Channels[0]|"], matches.Select(match => $"{match.Path}|{match.Property}"));
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
        var tags = MetadataSearch.GetTags(root, isPropertyIncluded: property => property.Name != nameof(MetadataSearchTestDevice.Channels));

        // Assert
        Assert.Equal(["floor-1", "floor-2", "Pumps", "Root"], tags);
    }

    [Fact]
    public void WhenATagWasEditedByHandToABlankOne_ThenItIsNotListed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new MetadataSearchTestFolder(context);
        TestMetadata.Set(root, """{ "tags": ["", " ", "pump"] }""");

        // Act
        var tags = MetadataSearch.GetTags(root);

        // Assert
        Assert.Equal(["pump"], tags);
    }

    // A storage below the root of the application, with its own device Pump beside a device Pump of the application root.
    private static (MetadataSearchTestFolder ApplicationRoot, MetadataSearchTestFolder Storage) CreateStorageTree(bool withResolver = true)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var storage = new MetadataSearchTestFolder(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Pump"] = new MetadataSearchTestDevice { Channels = [new MetadataSearchTestDevice()] } }
        };
        var applicationRoot = new MetadataSearchTestFolder(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage, ["Pump"] = new MetadataSearchTestDevice() }
        };
        if (withResolver)
        {
            var resolver = new SubjectPathResolver(() => applicationRoot);
            context.WithService<ISubjectPathResolver>(() => resolver);
        }

        return (applicationRoot, storage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData(" // ")]
    public void WhenThePathHasOnlyWhitespaceAndSlashes_ThenTheRootIsResolved(string path)
    {
        // Arrange
        var (_, storage) = CreateStorageTree();

        // Act
        var subject = MetadataSearch.ResolvePath(storage, path);

        // Assert
        Assert.Same(storage, subject);
    }

    [Theory]
    [InlineData("Pump")]
    [InlineData("/Pump")]
    [InlineData("//Pump")]
    [InlineData(" /Pump ")]
    public void WhenThePathHasLeadingSlashes_ThenItIsResolvedRelativeToTheRootAndNotToTheApplicationRoot(string path)
    {
        // Arrange
        var (_, storage) = CreateStorageTree();

        // Act
        var subject = MetadataSearch.ResolvePath(storage, path);

        // Assert
        Assert.Same(storage.Children["Pump"], subject);
    }

    [Fact]
    public void WhenThePathLeadsBelowASubject_ThenTheSubjectAtItIsResolved()
    {
        // Arrange
        var (_, storage) = CreateStorageTree();

        // Act
        var subject = MetadataSearch.ResolvePath(storage, "Pump/Channels[0]");

        // Assert
        Assert.Same(((MetadataSearchTestDevice)storage.Children["Pump"]).Channels[0], subject);
    }

    [Fact]
    public void WhenThePathStartsWithAParentSegment_ThenItDoesNotLeaveTheRoot()
    {
        // Arrange
        var (applicationRoot, storage) = CreateStorageTree();

        // Act
        var subject = MetadataSearch.ResolvePath(storage, "../Pump");

        // Assert
        Assert.NotSame(applicationRoot.Children["Pump"], subject);
        Assert.Null(subject);
    }

    [Fact]
    public void WhenNoSubjectIsAtThePath_ThenNullIsResolved()
    {
        // Arrange
        var (_, storage) = CreateStorageTree();

        // Act
        var subject = MetadataSearch.ResolvePath(storage, "Missing");

        // Assert
        Assert.Null(subject);
    }

    [Fact]
    public void WhenTheContextHasNoResolver_ThenNullIsResolvedForAPathAndTheRootForAnEmptyOne()
    {
        // Arrange
        var (_, storage) = CreateStorageTree(withResolver: false);

        // Act
        var subject = MetadataSearch.ResolvePath(storage, "Pump");
        var root = MetadataSearch.ResolvePath(storage, "/");

        // Assert
        Assert.Null(subject);
        Assert.Same(storage, root);
    }
}

[InterceptorSubject]
public partial class MetadataSearchTestFolder
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public MetadataSearchTestFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }
}

[InterceptorSubject]
public partial class MetadataSearchTestDevice
{
    public partial MetadataSearchTestDevice[] Channels { get; set; }

    public MetadataSearchTestDevice()
    {
        Channels = [];
    }
}
