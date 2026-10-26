using Namotion.Interceptor.Authorization.Internal;
using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class SubjectPathsTests
{
    private const int MaxPaths = 64;

    [Fact]
    public void WhenSubjectHasASingleChainOfParents_ThenItsPathIsWritten()
    {
        // Arrange
        var context = CreateContext();
        var grandfather = new Person(context);
        _ = new Person(context) { Father = new Person(context) { Father = grandfather } };
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(grandfather), DefaultPathProvider.Instance, paths, MaxPaths);

        // Assert
        Assert.True(complete);
        Assert.Equal([[new PathLink("Father", null, null), new PathLink("Father", null, null)]], paths);
    }

    [Fact]
    public void WhenSubjectHasTwoParents_ThenBothPathsAreWrittenInParentOrder()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person(context);
        var root = new Person(context) { Father = shared };
        root.Children = [shared];
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(shared), DefaultPathProvider.Instance, paths, MaxPaths);

        // Assert
        Assert.True(complete);
        Assert.Equal([[new PathLink("Father", null, null)], [new PathLink("Children", "0", null)]], paths);
    }

    [Fact]
    public void WhenAnAncestorAboveTheParentHasTwoParents_ThenAPathPerBranchIsWritten()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new Person(context);
        var middle = new Person(context) { Father = leaf };
        var root = new Person(context);
        root.Mother = middle;
        root.Children = [middle];
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(leaf), DefaultPathProvider.Instance, paths, MaxPaths);

        // Assert
        Assert.True(complete);
        Assert.Equal(
            [
                [new PathLink("Mother", null, null), new PathLink("Father", null, null)],
                [new PathLink("Children", "0", null), new PathLink("Father", null, null)]
            ],
            paths);
    }

    [Fact]
    public void WhenSubjectHasNoParents_ThenNoPathIsWrittenAndTheSetIsComplete()
    {
        // Arrange
        var root = new Person(CreateContext());
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(root), DefaultPathProvider.Instance, paths, MaxPaths);

        // Assert
        Assert.True(complete);
        Assert.Empty(paths);
    }

    [Fact]
    public void WhenAPropertyAboveTheParentIsExcluded_ThenTheOtherPathsAreWrittenAndTheSetIsIncomplete()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new Person(context);
        var middle = new Person(context) { Father = leaf };
        var root = new Person(context);
        root.Mother = middle;
        root.Children = [middle];
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(leaf), new ExcludingPathProvider(nameof(Person.Mother)), paths, MaxPaths);

        // Assert
        Assert.False(complete);
        Assert.Equal([[new PathLink("Children", "0", null), new PathLink("Father", null, null)]], paths);
    }

    [Fact]
    public void WhenParentsFormACycle_ThenOnlyTheFinitePathsAreWritten()
    {
        // Arrange
        var context = CreateContext();
        var first = new Person(context);
        var second = new Person(context) { Father = first };
        first.Father = second;
        _ = new Person(context) { Mother = first };
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(first), DefaultPathProvider.Instance, paths, MaxPaths);

        // Assert
        Assert.True(complete);
        Assert.Equal([[new PathLink("Mother", null, null)]], paths);
    }

    [Fact]
    public void WhenMoreChainsExistThanMaxPaths_ThenMaxPathsAreWrittenAndTheSetIsIncomplete()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person(context);
        var root = new Person(context) { Father = shared, Mother = shared };
        root.Children = [shared];
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(shared), DefaultPathProvider.Instance, paths, maxPaths: 2);

        // Assert
        Assert.False(complete);
        Assert.Equal([[new PathLink("Father", null, null)], [new PathLink("Mother", null, null)]], paths);
    }

    [Fact]
    public void WhenExactlyMaxPathsChainsExist_ThenAllAreWrittenAndTheSetIsComplete()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person(context);
        var root = new Person(context) { Father = shared, Mother = shared };
        root.Children = [shared];
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(shared), DefaultPathProvider.Instance, paths, maxPaths: 3);

        // Assert
        Assert.True(complete);
        Assert.Equal([[new PathLink("Father", null, null)], [new PathLink("Mother", null, null)], [new PathLink("Children", "0", null)]], paths);
    }

    [Fact]
    public void WhenParentsFormADenseComponentWithoutRoot_ThenTheSearchStopsAtItsBudget()
    {
        // Arrange
        var context = CreateContext();
        var items = Enumerable.Range(0, 8).Select(_ => new TestItem(context) { Value = "v" }).ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            var current = items[i];
            items[i].Children = items
                .Where(item => item != current)
                .Select((item, index) => (item, index))
                .ToDictionary(entry => entry.index.ToString(), entry => entry.item);
        }

        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(items[0]), DefaultPathProvider.Instance, paths, MaxPaths);

        // Assert
        Assert.False(complete);
        Assert.Empty(paths);
    }

    [Fact]
    public void WhenAnInlinePathsKeyResolvesBare_ThenItsLinkHasBothSpellings()
    {
        // Arrange
        var context = CreateContext();
        var light = new TestInlineContainer(context) { Name = "light" };
        var kitchen = new TestInlineContainer(context)
        {
            Name = "kitchen",
            Children = new Dictionary<string, TestInlineContainer> { ["Light"] = light }
        };
        _ = new TestInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<string, TestInlineContainer> { ["Kitchen"] = kitchen }
        };
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(light), DefaultPathProvider.Instance, paths, MaxPaths);

        // Assert
        Assert.True(complete);
        Assert.Equal([[new PathLink("Children", "Kitchen", "Kitchen"), new PathLink("Children", "Light", "Light")]], paths);
    }

    [Fact]
    public void WhenAnInlinePathsKeyIsShadowedByAProperty_ThenItsLinkHasOnlyTheExplicitSpelling()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "child" };
        _ = new TestInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<string, TestInlineContainer> { ["Name"] = child }
        };
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(child), DefaultPathProvider.Instance, paths, MaxPaths);

        // Assert
        Assert.True(complete);
        Assert.Equal([[new PathLink("Children", "Name", null)]], paths);
    }

    [Fact]
    public void WhenAnInlinePathsKeyIsShadowed_ThenTheOtherPathsAreWrittenAndTheSetIsIncomplete()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestShadowedInlineContainer(context) { Name = "child", Label = "label" };
        var middle = new TestShadowedInlineContainer(context)
        {
            Name = "middle",
            Label = "label",
            Children = new Dictionary<string, TestShadowedInlineContainer> { ["a.b"] = child }
        };
        _ = new TestShadowedInlineContainer(context)
        {
            Name = "root",
            Label = "label",
            Children = new Dictionary<string, TestShadowedInlineContainer> { ["CNC01"] = child, ["Middle"] = middle }
        };
        var paths = new List<PathLink[]>();

        // Act
        var complete = SubjectPaths.TryGetPaths(Registered(child), pathProvider, paths, MaxPaths);

        // Assert
        Assert.False(complete);
        Assert.Equal([[new PathLink(null, "CNC01", "CNC01")]], paths);
    }

    // The cross-checks below guard the link rules against drift from the registry's path reader and writer: every
    // spelling a link offers must resolve to the subject, the spellings it leaves out must not, and the canonical
    // spelling of the first path must be what TryGetPath writes.

    [Fact]
    public void WhenLinksArePlainProperties_ThenTheyMatchTheRegistry()
    {
        // Arrange
        var context = CreateContext();
        var grandfather = new Person(context);
        var root = new Person(context) { Father = new Person(context) { Mother = grandfather } };

        // Act & Assert
        AssertLinksMatchRegistry(root, grandfather, DefaultPathProvider.Instance, [[new PathLink("Father", null, null), new PathLink("Mother", null, null)]]);
    }

    [Fact]
    public void WhenLinkIsADictionaryEntry_ThenItMatchesTheRegistry()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestItem(context);
        var root = new TestItem(context) { Children = new Dictionary<string, TestItem> { ["Entry"] = child } };

        // Act & Assert
        AssertLinksMatchRegistry(root, child, DefaultPathProvider.Instance, [[new PathLink("Children", "Entry", null)]], unofferedSpellings: ["Entry"]);
    }

    [Fact]
    public void WhenLinkIsACollectionPosition_ThenItMatchesTheRegistry()
    {
        // Arrange
        var context = CreateContext();
        var child = new Person(context);
        var root = new Person(context) { Children = [new Person(context), child] };

        // Act & Assert
        AssertLinksMatchRegistry(root, child, DefaultPathProvider.Instance, [[new PathLink("Children", "1", null)]], unofferedSpellings: ["1"]);
    }

    [Fact]
    public void WhenLinksAreBareInlinePathsKeys_ThenEveryCombinationOfSpellingsMatchesTheRegistry()
    {
        // Arrange
        var context = CreateContext();
        var light = new TestInlineContainer(context);
        var root = new TestInlineContainer(context)
        {
            Children = new Dictionary<string, TestInlineContainer>
            {
                ["Kitchen"] = new TestInlineContainer(context) { Children = new Dictionary<string, TestInlineContainer> { ["Light"] = light } }
            }
        };

        // Act & Assert
        AssertLinksMatchRegistry(root, light, DefaultPathProvider.Instance,
            [[new PathLink("Children", "Kitchen", "Kitchen"), new PathLink("Children", "Light", "Light")]]);
    }

    [Fact]
    public void WhenAnInlinePathsKeyIsShadowedByAProperty_ThenItsExplicitSpellingMatchesTheRegistry()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context);
        var root = new TestInlineContainer(context) { Children = new Dictionary<string, TestInlineContainer> { ["Name"] = child } };

        // Act & Assert
        AssertLinksMatchRegistry(root, child, DefaultPathProvider.Instance, [[new PathLink("Children", "Name", null)]], unofferedSpellings: ["Name"]);
    }

    [Theory]
    [InlineData("a]b", true)]
    [InlineData("'q", true)]
    [InlineData("x y", true)]
    [InlineData("", false)]
    [InlineData("a.b", false)]
    [InlineData("a[b", false)]
    public void WhenAnInlinePathsKeyNeedsQuotingOrHasSyntaxCharacters_ThenItsSpellingsMatchTheRegistry(string key, bool isReadBare)
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context);
        var root = new TestInlineContainer(context) { Children = new Dictionary<string, TestInlineContainer> { [key] = child } };
        string[] unofferedSpellings = isReadBare || key.Length == 0 ? [] : [key];

        // Act & Assert
        AssertLinksMatchRegistry(root, child, DefaultPathProvider.Instance, [[new PathLink("Children", key, isReadBare ? key : null)]], unofferedSpellings);
    }

    [Fact]
    public void WhenTheExplicitFormOfAnInlinePathsKeyIsShadowed_ThenOnlyItsBareSpellingMatchesTheRegistry()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestShadowedInlineContainer(context);
        var root = new TestShadowedInlineContainer(context)
        {
            Children = new Dictionary<string, TestShadowedInlineContainer> { ["CNC01"] = child }
        };

        // Act & Assert
        AssertLinksMatchRegistry(root, child, new AttributeBasedPathProvider("test"), [[new PathLink(null, "CNC01", "CNC01")]],
            unofferedSpellings: ["Children[CNC01]"]);
    }

    [Fact]
    public void WhenAPathMixesLinkShapes_ThenEveryCombinationOfSpellingsMatchesTheRegistry()
    {
        // Arrange
        var home = new Home(CreateContext());
        var lamp = new Light();
        var bedroom = new Room { Lights = new Dictionary<string, Light> { ["Lamp"] = lamp } };
        home.Areas = new Dictionary<string, Room> { ["Kitchen"] = new Room { Neighbor = bedroom } };

        // Act & Assert
        AssertLinksMatchRegistry(home, lamp, SlashPathProvider.Instance,
            [[new PathLink("Areas", "Kitchen", "Kitchen"), new PathLink("Neighbor", null, null), new PathLink("Lights", "Lamp", null)]]);
    }

    [Fact]
    public void WhenLinkIsASubjectHoldingAttribute_ThenItMatchesTheRegistry()
    {
        // Arrange
        var home = TestGraph.Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var probe = new Light();
        sensor.Temperature_Probe = probe;

        // Act & Assert
        AssertLinksMatchRegistry(home, probe, SlashPathProvider.Instance,
            [[new PathLink("Rooms", "Kitchen", null), new PathLink("Sensor", null, null), new PathLink("Probe", null, null)]]);
    }

    private static void AssertLinksMatchRegistry(
        IInterceptorSubject root, IInterceptorSubject subject, PathProviderBase pathProvider, PathLink[][] expectedPaths,
        string[]? unofferedSpellings = null)
    {
        var paths = new List<PathLink[]>();
        var registeredRoot = Registered(root);
        var registeredSubject = Registered(subject);

        Assert.True(SubjectPaths.TryGetPaths(registeredSubject, pathProvider, paths, MaxPaths));
        Assert.Equal(expectedPaths, paths);

        foreach (var spelling in paths.SelectMany(path => GetSpellings(path, pathProvider)))
        {
            Assert.Same(subject, pathProvider.TryGetSubjectFromPath(registeredRoot, spelling)?.Subject);
        }

        foreach (var spelling in unofferedSpellings ?? [])
        {
            Assert.NotSame(subject, pathProvider.TryGetSubjectFromPath(registeredRoot, spelling)?.Subject);
        }

        var firstParent = registeredSubject.Parents[0];
        Assert.Equal(firstParent.Property.TryGetPath(pathProvider, rootSubject: null, firstParent.Index), GetCanonicalSpelling(paths[0], pathProvider));
    }

    // Every combination of the spellings of the links.
    private static List<string> GetSpellings(PathLink[] path, PathProviderBase pathProvider)
    {
        List<string> spellings = [""];
        foreach (var link in path)
        {
            var linkSpellings = GetLinkSpellings(link, pathProvider);
            spellings = spellings
                .SelectMany(prefix => linkSpellings.Select(spelling => prefix.Length == 0 ? spelling : prefix + pathProvider.PathSeparator + spelling))
                .ToList();
        }

        return spellings;
    }

    private static List<string> GetLinkSpellings(PathLink link, PathProviderBase pathProvider)
    {
        var spellings = new List<string>();
        if (link.BareKey is not null)
        {
            spellings.Add(link.BareKey);
        }

        if (link.Segment is not null)
        {
            spellings.Add(GetExplicitSpelling(link, pathProvider));
        }

        return spellings;
    }

    private static string GetCanonicalSpelling(PathLink[] path, PathProviderBase pathProvider)
        => string.Join(pathProvider.PathSeparator, path.Select(link => link.BareKey ?? GetExplicitSpelling(link, pathProvider)));

    private static string GetExplicitSpelling(PathLink link, PathProviderBase pathProvider)
        => link.Index is null ? link.Segment! : $"{link.Segment}{pathProvider.IndexOpen}{Quote(link.Index, pathProvider)}{pathProvider.IndexClose}";

    // The registry's writer quotes a key that is empty, starts with a quote or contains the index close character,
    // doubling the quotes inside.
    private static string Quote(string index, PathProviderBase pathProvider)
        => index.Length == 0 || index[0] == '\'' || index.Contains(pathProvider.IndexClose)
            ? $"'{index.Replace("'", "''")}'"
            : index;

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != excludedPropertyName;
    }
}
