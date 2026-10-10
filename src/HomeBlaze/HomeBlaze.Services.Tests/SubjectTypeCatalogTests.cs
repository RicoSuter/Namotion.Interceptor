using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Tests;

public class SubjectTypeCatalogTests
{
    private static SubjectTypeCatalog CreateCatalog(params Type[] types)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes(types);
        return new SubjectTypeCatalog(new SubjectTypeRegistry(typeProvider));
    }

    private static IReadOnlyList<SubjectTypeDescriptor> DescribeAll()
    {
        return CreateCatalog(
                typeof(CatalogAnnotatedSubject),
                typeof(CatalogSecondDeviceSubject),
                typeof(CatalogServerSubject),
                typeof(CatalogPlainSubject))
            .GetCreatableTypes([]);
    }

    [Fact]
    public void WhenTypeIsNotConfigurable_ThenItIsNotListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogPlainSubject), typeof(CatalogNotConfigurableSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([]);

        // Assert
        Assert.Equal([typeof(CatalogPlainSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenTypeIsExcludedFromCreation_ThenItIsNotListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogPlainSubject), typeof(CatalogExcludedSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([]);

        // Assert
        Assert.Equal([typeof(CatalogPlainSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenSingleInstanceTypeHasNoInstance_ThenItIsListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogSingleInstanceSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([new CatalogPlainSubject()]);

        // Assert
        Assert.Equal([typeof(CatalogSingleInstanceSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenSingleInstanceTypeHasAnInstance_ThenItIsNotListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogSingleInstanceSubject), typeof(CatalogPlainSubject));
        IInterceptorSubject[] existingSubjects = [new CatalogSingleInstanceSubject(), new CatalogPlainSubject()];

        // Act
        var descriptors = catalog.GetCreatableTypes(existingSubjects);

        // Assert
        Assert.Equal([typeof(CatalogPlainSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenTypeHasAllAttributes_ThenDescriptorUsesThem()
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(typeof(CatalogAnnotatedSubject));

        // Assert
        Assert.Equal("Zebra sensor", descriptor.DisplayName);
        Assert.Equal("Counts stripes", descriptor.Description);
        Assert.Equal("Devices", descriptor.Group);
        Assert.Equal("Pets", descriptor.IconName);
    }

    [Fact]
    public void WhenTypeHasNoAttributes_ThenDescriptorUsesFallbacks()
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(typeof(CatalogPlainSubject));

        // Assert
        Assert.Equal("Catalog Plain Subject", descriptor.DisplayName);
        Assert.Null(descriptor.Description);
        Assert.Equal(SubjectTypeCatalog.OtherGroup, descriptor.Group);
        Assert.Equal("Category", descriptor.IconName);
    }

    [Fact]
    public void WhenTypeHasCategoryButNoIcon_ThenGroupIconIsUsed()
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(typeof(CatalogServerSubject));

        // Assert
        Assert.Equal("Dns", descriptor.IconName);
    }

    [Fact]
    public void WhenTypesAreListed_ThenGroupsAreAlphabeticalWithOtherLastAndTypesAreSortedByDisplayName()
    {
        // Act
        var descriptors = DescribeAll();

        // Assert
        Assert.Equal(
            ["Alpha relay", "Zebra sensor", "Catalog Server Subject", "Catalog Plain Subject"],
            descriptors.Select(descriptor => descriptor.DisplayName));
        Assert.Equal(
            ["Devices", "Devices", "Servers", "Other"],
            descriptors.Select(descriptor => descriptor.Group));
    }

    [Theory]
    [InlineData("zebra")]
    [InlineData("CatalogAnnotated")]
    [InlineData("stripes")]
    [InlineData("  ZEBRA   devices ")]
    public void WhenSearchMatchesNameClassDescriptionOrGroup_ThenTypeIsFound(string searchText)
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var matches = SubjectTypeCatalog.Filter(descriptors, searchText, group: null);

        // Assert
        Assert.Contains(matches, descriptor => descriptor.Type == typeof(CatalogAnnotatedSubject));
        Assert.DoesNotContain(matches, descriptor => descriptor.Type == typeof(CatalogServerSubject));
    }

    [Fact]
    public void WhenSearchIsEmpty_ThenAllTypesAreReturned()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var matches = SubjectTypeCatalog.Filter(descriptors, searchText: " ", group: null);

        // Assert
        Assert.Equal(descriptors, matches);
    }

    [Fact]
    public void WhenGroupIsSelected_ThenOnlyThatGroupIsReturned()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var matches = SubjectTypeCatalog.Filter(descriptors, searchText: null, group: "Devices");

        // Assert
        Assert.Equal(
            [typeof(CatalogSecondDeviceSubject), typeof(CatalogAnnotatedSubject)],
            matches.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenSearching_ThenGroupCountsFollowTheSearch()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var groups = SubjectTypeCatalog.GetGroups(descriptors, "zebra");

        // Assert
        Assert.Equal(
            [new SubjectTypeGroup("Devices", 1), new SubjectTypeGroup("Servers", 0), new SubjectTypeGroup("Other", 0)],
            groups);
    }

    [Fact]
    public void WhenNameIsFree_ThenTypeNameIsSuggested()
    {
        // Act
        var name = SubjectTypeCatalog.SuggestName(typeof(CatalogPlainSubject), ["Other", "Dashboard.md"], ".json");

        // Assert
        Assert.Equal("CatalogPlainSubject", name);
    }

    [Fact]
    public void WhenNameIsTaken_ThenTheNextFreeNumberIsAppended()
    {
        // Arrange
        string[] existingNames = ["CatalogPlainSubject", "catalogplainsubject2.json"];

        // Act
        var name = SubjectTypeCatalog.SuggestName(typeof(CatalogPlainSubject), existingNames, ".json");

        // Assert
        Assert.Equal("CatalogPlainSubject3", name);
    }

    [Theory]
    [InlineData("history", true)]
    [InlineData("HISTORY", true)]
    [InlineData("Dashboard", false)]
    [InlineData("Other", false)]
    public void WhenExistingNameMatchesWithOrWithoutSuffixIgnoringCase_ThenNameIsTaken(string name, bool expected)
    {
        // Arrange
        string[] existingNames = ["History", "Dashboard.md"];

        // Act
        var isTaken = SubjectTypeCatalog.IsNameTaken(name, existingNames, ".json");

        // Assert
        Assert.Equal(expected, isTaken);
    }
}
