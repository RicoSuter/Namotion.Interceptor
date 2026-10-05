using System.Collections.Immutable;
using System.Globalization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathKeyResolutionTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static IInterceptorSubject? ResolveSubject(IInterceptorSubject root, string path)
        => DefaultPathProvider.Instance.TryGetSubjectFromPath(root.TryGetRegisteredSubject()!, path)?.Subject;

    private static (RegisteredSubjectProperty Property, object? Index)? ResolveProperty(IInterceptorSubject root, string path)
        => DefaultPathProvider.Instance.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path);

    [Fact]
    public void WhenStringKeyLooksLikeInteger_ThenPathResolvesToThatKey()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "five" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["5"] = item;

        // Act
        var subject = ResolveSubject(container, "Items[5]");
        var property = ResolveProperty(container, "Items[5]");

        // Assert
        Assert.Same(item, subject);
        Assert.Equal((object)"5", property?.Index);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(42)]
    public void WhenDictionaryHasIntKeys_ThenPathResolvesWithTypedIndex(int key)
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestKeyedContainer(context);
        container.ByNumber[key] = item;
        var path = string.Create(CultureInfo.InvariantCulture, $"ByNumber[{key}]");

        // Act
        var subject = ResolveSubject(container, path);
        var property = ResolveProperty(container, path);

        // Assert
        Assert.Same(item, subject);
        Assert.Equal((object)key, property?.Index);
    }

    [Theory]
    [InlineData("ByNumber[+3]")]
    [InlineData("ByNumber[03]")]
    [InlineData("ByNumber[ 3]")]
    [InlineData("ByNumber[three]")]
    public void WhenIntKeyTextIsNotCanonical_ThenPathDoesNotResolve(string path)
    {
        // Arrange
        var context = CreateContext();
        var container = new TestKeyedContainer(context);
        container.ByNumber[3] = new TestItem(context) { Value = "v" };

        // Act
        var property = ResolveProperty(container, path);

        // Assert
        Assert.Null(property);
    }

    [Fact]
    public void WhenIntKeyIsAbsent_ThenPropertyPathReturnsTypedIndex()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestKeyedContainer(context);

        // Act
        var property = ResolveProperty(container, "ByNumber[9]");
        var subject = ResolveSubject(container, "ByNumber[9]");

        // Assert
        Assert.Equal("ByNumber", property?.Property.Name);
        Assert.Equal((object)9, property?.Index);
        Assert.Null(subject);
    }

    [Theory]
    [InlineData("ByColor[Blue]", true)]
    [InlineData("ByColor[4]", false)]
    [InlineData("ByColor[blue]", false)]
    public void WhenDictionaryHasEnumKeys_ThenOnlyTheNameResolves(string path, bool expectedResolved)
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestKeyedContainer(context);
        container.ByColor[TestColor.Blue] = item;

        // Act
        var subject = ResolveSubject(container, path);

        // Assert
        Assert.Same(expectedResolved ? item : null, subject);
    }

    [Fact]
    public void WhenDictionaryHasFlagsKey_ThenTryGetPathRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestKeyedContainer(context)
        {
            ByPermissions = new Dictionary<TestPermissions, TestItem> { [TestPermissions.Read | TestPermissions.Write] = item }
        };
        var valueProperty = item.TryGetRegisteredSubject()!.TryGetProperty("Value")!;

        // Act
        var path = valueProperty.TryGetPath(DefaultPathProvider.Instance, container);
        var property = ResolveProperty(container, path!);

        // Assert
        Assert.Equal("ByPermissions[Read, Write].Value", path);
        Assert.Same(item, property?.Property.Subject);
    }

    [Fact]
    public void WhenDictionaryHasGuidKeys_ThenOnlyTheCanonicalFormResolves()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestKeyedContainer(context);
        container.ById[Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")] = item;

        // Act
        var canonical = ResolveSubject(container, "ById[0f8fad5b-d9cb-469f-a165-70867728950e]");
        var upperCase = ResolveSubject(container, "ById[0F8FAD5B-D9CB-469F-A165-70867728950E]");

        // Assert
        Assert.Same(item, canonical);
        Assert.Null(upperCase);
    }

    [Fact]
    public void WhenDictionaryHasObjectKeys_ThenKeysResolveByTheirText()
    {
        // Arrange
        var context = CreateContext();
        var numbered = new TestItem(context) { Value = "numbered" };
        var named = new TestItem(context) { Value = "named" };
        var container = new TestKeyedContainer(context);
        container.ByAnything[42] = numbered;
        container.ByAnything["x"] = named;

        // Act
        var numberedSubject = ResolveSubject(container, "ByAnything[42]");
        var numberedProperty = ResolveProperty(container, "ByAnything[42]");
        var namedSubject = ResolveSubject(container, "ByAnything[x]");
        var absentProperty = ResolveProperty(container, "ByAnything[y]");

        // Assert
        Assert.Same(numbered, numberedSubject);
        Assert.Equal((object)42, numberedProperty?.Index);
        Assert.Same(named, namedSubject);
        Assert.Equal((object)"y", absentProperty?.Index);
    }

    [Fact]
    public void WhenImmutableDictionaryHasIntKeys_ThenPresentKeyResolvesAndAbsentKeyDoesNot()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestKeyedContainer(context)
        {
            ReadOnlyByNumber = new Dictionary<int, TestItem> { [1] = item }.ToImmutableDictionary()
        };

        // Act
        var present = ResolveSubject(container, "ReadOnlyByNumber[1]");
        var absent = ResolveSubject(container, "ReadOnlyByNumber[2]");

        // Assert
        Assert.Same(item, present);
        Assert.Null(absent);
    }

    [Theory]
    [InlineData("Items[1]", true)]
    [InlineData("Items[01]", false)]
    [InlineData("Items[2]", false)]
    [InlineData("Items[-1]", false)]
    public void WhenCollectionPositionIsResolved_ThenOnlyCanonicalPositionsInRangeResolve(string path, bool expectedResolved)
    {
        // Arrange
        var context = CreateContext();
        var first = new TestItem(context) { Value = "first" };
        var second = new TestItem(context) { Value = "second" };
        var container = new TestKeyedContainer(context) { Items = new List<TestItem> { first, second } };

        // Act
        var subject = ResolveSubject(container, path);

        // Assert
        Assert.Same(expectedResolved ? second : null, subject);
    }

    [Fact]
    public void WhenIndexIsOnScalarProperty_ThenPathDoesNotResolve()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };

        // Act
        var property = ResolveProperty(container, "Name[x]");

        // Assert
        Assert.Null(property);
    }

    [Fact]
    public void WhenInlineDictionaryHasIntKeys_ThenPathRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestNumberedInlineContainer(context) { Name = "Child" };
        var root = new TestNumberedInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<int, TestNumberedInlineContainer> { [5] = child }
        };
        var nameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(DefaultPathProvider.Instance, root);
        var subject = ResolveSubject(root, "5");
        var property = ResolveProperty(root, path!);

        // Assert
        Assert.Equal("5.Name", path);
        Assert.Same(child, subject);
        Assert.Same(child, property?.Property.Subject);
    }

    [Fact]
    public void WhenCollectionPositionIsResolved_ThenOnlyTheNameAndTheBoxedPositionAreAllocated()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestKeyedContainer(context)
        {
            Items = new List<TestItem> { new(context) { Value = "a" }, new(context) { Value = "b" } }
        };
        var root = container.TryGetRegisteredSubject()!;
        var boxedPositionSize = 3 * IntPtr.Size;
        var pathWithoutIndex = "Items.";

        // Act
        // The trailing separator makes the name a substring as in the indexed path; a name spanning the whole
        // path is the path string itself.
        var nameSize = MeasureAllocatedBytes(() => pathWithoutIndex.Substring(0, 5).Length == 5);
        var withoutIndex = MeasureAllocatedBytes(() => Resolve(root, pathWithoutIndex));
        var withIndex = MeasureAllocatedBytes(() => Resolve(root, "Items[1]"));

        // Assert
        Assert.Equal(nameSize, withoutIndex);
        Assert.Equal(boxedPositionSize, withIndex - withoutIndex);
    }

    private static bool Resolve(RegisteredSubject root, string path)
        => DefaultPathProvider.Instance.TryGetPropertyFromPath(root, path) is not null;

    /// <summary>
    /// The smallest allocation of several calls, so one-time warm-up allocations do not count.
    /// </summary>
    private static long MeasureAllocatedBytes(Func<bool> action)
    {
        var minimum = long.MaxValue;
        for (var i = 0; i < 10; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var succeeded = action();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(succeeded);
            minimum = Math.Min(minimum, allocated);
        }

        return minimum;
    }
}
