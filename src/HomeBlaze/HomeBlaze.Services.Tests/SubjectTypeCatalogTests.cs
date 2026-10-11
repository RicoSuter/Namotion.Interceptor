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

    private static (string Name, int MatchCount)[] CountMatches(IReadOnlyList<SubjectTypeGroup> groups)
    {
        return groups.Select(group => (group.Name, group.Matches.Count)).ToArray();
    }

    private static IEnumerable<SubjectTypeDescriptor> GetMatches(IReadOnlyList<SubjectTypeGroup> groups)
    {
        return groups.SelectMany(group => group.Matches);
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
    public void WhenTypeIsAbstract_ThenItIsNotListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogPlainSubject), typeof(CatalogAbstractSubject));

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
        Assert.Equal("Catalog plain subject", descriptor.DisplayName);
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
            ["Alpha relay", "Zebra sensor", "Catalog server subject", "Catalog plain subject"],
            descriptors.Select(descriptor => descriptor.DisplayName));
        Assert.Equal(
            ["Devices", "Devices", "Servers", "Other"],
            descriptors.Select(descriptor => descriptor.Group));
    }

    [Fact]
    public void WhenCategoriesDifferOnlyInCase_ThenTheyFormOneGroupNamedAfterTheFirstOccurrence()
    {
        // Arrange
        var catalog = CreateCatalog(
            typeof(CatalogAnnotatedSubject),
            typeof(CatalogSecondDeviceSubject),
            typeof(CatalogLowerCaseGroupSubject),
            typeof(CatalogServerSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([]);
        var groups = SubjectTypeCatalog.GetGroups(descriptors, searchText: null);

        // Assert
        Assert.Equal(
            ["Aardvark feeder", "Alpha relay", "Zebra sensor", "Catalog server subject"],
            descriptors.Select(descriptor => descriptor.DisplayName));
        Assert.Equal(
            ["devices", "devices", "devices", "Servers"],
            descriptors.Select(descriptor => descriptor.Group));
        Assert.Equal([("devices", 3), ("Servers", 1)], CountMatches(groups));
        Assert.Equal(
            [typeof(CatalogLowerCaseGroupSubject), typeof(CatalogSecondDeviceSubject), typeof(CatalogAnnotatedSubject)],
            groups[0].Matches.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenDescriptorsSpellAGroupDifferently_ThenTheyAreCountedAsOneGroup()
    {
        // Arrange
        IReadOnlyList<SubjectTypeDescriptor> descriptors =
        [
            SubjectTypeCatalog.Describe(typeof(CatalogAnnotatedSubject)),
            SubjectTypeCatalog.Describe(typeof(CatalogLowerCaseGroupSubject))
        ];

        // Act
        var groups = SubjectTypeCatalog.GetGroups(descriptors, searchText: null);

        // Assert
        Assert.Equal([("Devices", 2)], CountMatches(groups));
    }

    [Theory]
    [InlineData("zebra")]
    [InlineData("CatalogAnnotated")]
    [InlineData("stripes")]
    [InlineData("  ZEBRA   devices ")]
    [InlineData("zebra\tdevices\nstripes")]
    public void WhenSearchMatchesNameClassDescriptionOrGroup_ThenTypeIsFound(string searchText)
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var matches = GetMatches(SubjectTypeCatalog.GetGroups(descriptors, searchText)).ToList();

        // Assert
        Assert.Contains(matches, descriptor => descriptor.Type == typeof(CatalogAnnotatedSubject));
        Assert.DoesNotContain(matches, descriptor => descriptor.Type == typeof(CatalogServerSubject));
    }

    [Theory]
    [InlineData("zebra servers")]
    [InlineData("stripes relay")]
    [InlineData("devices nothing")]
    public void WhenOnlySomeSearchTermsMatchAType_ThenItIsNotFound(string searchText)
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var groups = SubjectTypeCatalog.GetGroups(descriptors, searchText);

        // Assert - each term on its own matches a type, or the group of one.
        Assert.Empty(GetMatches(groups));
        Assert.Equal(["Devices", "Servers", "Other"], groups.Select(group => group.Name));
    }

    [Fact]
    public void WhenSearchIsEmpty_ThenAllTypesAreReturned()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var groups = SubjectTypeCatalog.GetGroups(descriptors, searchText: " ");

        // Assert
        Assert.Equal(descriptors, GetMatches(groups));
    }

    [Fact]
    public void WhenTypesAreGrouped_ThenEachGroupHoldsOnlyItsOwnTypesInTheListedOrder()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var groups = SubjectTypeCatalog.GetGroups(descriptors, searchText: null);

        // Assert
        Assert.Equal(["Devices", "Servers", "Other"], groups.Select(group => group.Name));
        Assert.Equal(
            [typeof(CatalogSecondDeviceSubject), typeof(CatalogAnnotatedSubject)],
            groups[0].Matches.Select(descriptor => descriptor.Type));
        Assert.Equal([typeof(CatalogServerSubject)], groups[1].Matches.Select(descriptor => descriptor.Type));
        Assert.Equal([typeof(CatalogPlainSubject)], groups[2].Matches.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenSearching_ThenGroupCountsFollowTheSearch()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var groups = SubjectTypeCatalog.GetGroups(descriptors, "zebra");

        // Assert
        Assert.Equal([("Devices", 1), ("Servers", 0), ("Other", 0)], CountMatches(groups));
        Assert.Equal([typeof(CatalogAnnotatedSubject)], groups[0].Matches.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenTypeDerivesFromAnAnnotatedType_ThenItDoesNotInheritTheDescription()
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(typeof(CatalogDerivedAnnotatedSubject));

        // Assert
        Assert.Equal("Catalog derived annotated subject", descriptor.DisplayName);
        Assert.Null(descriptor.Description);
        Assert.Equal(SubjectTypeCatalog.OtherGroup, descriptor.Group);
        Assert.Equal("Category", descriptor.IconName);
    }

    [Fact]
    public void WhenTypeDerivesFromAnExcludedType_ThenItIsListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogExcludedSubject), typeof(CatalogDerivedExcludedSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([]);

        // Assert
        Assert.Equal([typeof(CatalogDerivedExcludedSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenOnlyADerivedTypeOfASingleInstanceTypeHasAnInstance_ThenBothAreListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogSingleInstanceSubject), typeof(CatalogDerivedSingleInstanceSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([new CatalogDerivedSingleInstanceSubject()]);

        // Assert - the derived type is not a single instance type itself, and its instance is none of the base type.
        Assert.Equal(
            [typeof(CatalogDerivedSingleInstanceSubject), typeof(CatalogSingleInstanceSubject)],
            descriptors.Select(descriptor => descriptor.Type));
    }

    [Theory]
    [InlineData(typeof(Relay), "Relay")]
    [InlineData(typeof(ShellyDevice), "Shelly device")]
    [InlineData(typeof(HTTPServer), "HTTP server")]
    [InlineData(typeof(RemoteIOModule), "Remote IO module")]
    [InlineData(typeof(Sensor2Gateway), "Sensor2 gateway")]
    [InlineData(typeof(Mqtt5IOTBridge), "Mqtt5 IOT bridge")]
    public void WhenTypeHasNoDisplayName_ThenTheClassNameIsSplitIntoWordsInSentenceCase(Type type, string expectedDisplayName)
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(type);

        // Assert
        Assert.Equal(expectedDisplayName, descriptor.DisplayName);
    }

    [Theory]
    [InlineData(typeof(CatalogEmptyAttributesSubject), "Catalog empty attributes subject")]
    [InlineData(typeof(CatalogBlankAttributesSubject), "Catalog blank attributes subject")]
    public void WhenAttributeValuesAreEmptyOrWhitespace_ThenDescriptorUsesFallbacks(Type type, string expectedDisplayName)
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(type);

        // Assert
        Assert.Equal(expectedDisplayName, descriptor.DisplayName);
        Assert.Null(descriptor.Description);
        Assert.Equal(SubjectTypeCatalog.OtherGroup, descriptor.Group);
        Assert.Equal("Category", descriptor.IconName);
    }

    private sealed class Relay;

    private sealed class ShellyDevice;

    private sealed class HTTPServer;

    private sealed class RemoteIOModule;

    private sealed class Sensor2Gateway;

    private sealed class Mqtt5IOTBridge;
}
