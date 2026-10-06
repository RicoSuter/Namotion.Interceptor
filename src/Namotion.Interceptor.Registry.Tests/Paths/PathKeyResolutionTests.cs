using System.Collections.Immutable;
using System.Globalization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathKeyResolutionTests
{
    private static readonly Guid KnownGuid = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static IInterceptorSubject? ResolveSubject(IInterceptorSubject root, string path)
        => DefaultPathProvider.Instance.TryGetSubjectFromPath(root.TryGetRegisteredSubject()!, path)?.Subject;

    private static (RegisteredSubjectProperty Property, object? Index)? ResolveProperty(IInterceptorSubject root, string path)
        => DefaultPathProvider.Instance.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path);

    private static string? GetValuePath(TestItem item)
        => item.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestItem.Value))!.TryGetPath(DefaultPathProvider.Instance, null);

    [Fact]
    public void WhenStringKeyLooksLikeInteger_ThenPathResolvesToThatKey()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "five" };
        var container = new TestContainer(context) { Items = new Dictionary<string, TestItem> { ["5"] = item } };

        // Act
        var subject = ResolveSubject(container, "Items[5]");
        var property = ResolveProperty(container, "Items[5]");

        // Assert
        Assert.Same(item, subject);
        Assert.Equal((object)"5", property?.Index);
    }

    [Theory]
    [InlineData("ByNumber[42]", true)]
    [InlineData("ByNumber[-3]", true)]
    [InlineData("ByNumber[+3]", false)]
    [InlineData("ByNumber[03]", false)]
    [InlineData("ByNumber[ 3]", false)]
    [InlineData("ByNumber[abc]", false)]
    [InlineData("ByNumber[0]", true)]
    [InlineData("ByNumber[-0]", false)]
    [InlineData("ByNumber[-03]", false)]
    [InlineData("ByColor[Blue]", true)]
    [InlineData("ByColor[4]", false)]
    [InlineData("ByColor[blue]", false)]
    [InlineData("ById[0f8fad5b-d9cb-469f-a165-70867728950e]", true)]
    [InlineData("ById[0F8FAD5B-D9CB-469F-A165-70867728950E]", false)]
    [InlineData("ById[0f8fad5bd9cb469fa16570867728950e]", false)]
    public void WhenTypedKeyIsResolved_ThenOnlyTheWrittenFormResolves(string path, bool expectedResolved)
    {
        // Arrange
        var context = CreateContext();
        var container = new TestKeyedContainer(context)
        {
            ByNumber = new Dictionary<int, TestItem> { [42] = new(context), [-3] = new(context), [3] = new(context), [0] = new(context) },
            ByColor = new Dictionary<TestColor, TestItem> { [TestColor.Blue] = new(context) },
            ById = new Dictionary<Guid, TestItem> { [KnownGuid] = new(context) }
        };

        // Act
        var subject = ResolveSubject(container, path);

        // Assert
        Assert.Equal(expectedResolved, subject is not null);
    }

    [Fact]
    public void WhenQuotedKeyAddressesIntegerKey_ThenItResolves()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context);
        var container = new TestKeyedContainer(context) { ByNumber = new Dictionary<int, TestItem> { [5] = item } };

        // Act
        var subject = ResolveSubject(container, "ByNumber['5']");

        // Assert
        Assert.Same(item, subject);
    }

    [Fact]
    public void WhenIntKeyIsAbsent_ThenPropertyPathReturnsTypedIndex()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestKeyedContainer(context);

        // Act
        var property = ResolveProperty(container, "ByNumber[7]");

        // Assert
        Assert.Equal(nameof(TestKeyedContainer.ByNumber), property?.Property.Name);
        Assert.Equal((object)7, property?.Index);
    }

    [Fact]
    public void WhenFlagsKeyIsWritten_ThenPathRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestKeyedContainer(context)
        {
            ByPermissions = new Dictionary<TestPermissions, TestItem> { [TestPermissions.Read | TestPermissions.Write] = item }
        };

        // Act
        var path = GetValuePath(item);

        // Assert
        Assert.Equal("ByPermissions[Read, Write].Value", path);
        Assert.Same(item, ResolveProperty(container, path!)?.Property.Subject);
    }

    [Fact]
    public void WhenGuidAndEnumKeysAreWritten_ThenPathsResolveBackToTheSameItems()
    {
        // Arrange
        var context = CreateContext();
        var byId = new TestItem(context) { Value = "id" };
        var byColor = new TestItem(context) { Value = "color" };
        var container = new TestKeyedContainer(context)
        {
            ById = new Dictionary<Guid, TestItem> { [KnownGuid] = byId },
            ByColor = new Dictionary<TestColor, TestItem> { [TestColor.Green] = byColor }
        };

        // Act
        var idPath = GetValuePath(byId)!;
        var colorPath = GetValuePath(byColor)!;

        // Assert
        Assert.Equal("ById[0f8fad5b-d9cb-469f-a165-70867728950e].Value", idPath);
        Assert.Equal("ByColor[Green].Value", colorPath);
        Assert.Same(byId, ResolveProperty(container, idPath)?.Property.Subject);
        Assert.Same(byColor, ResolveProperty(container, colorPath)?.Property.Subject);
    }

    [Fact]
    public void WhenFallbackKeyedDictionaryHasNoMatchingEntry_ThenIndexIsTheText()
    {
        // Arrange
        var context = CreateContext();
        var keyed = new TestKeyedContainer(context) { ByAnything = new Dictionary<object, TestItem> { [5] = new(context) } };
        var doubleKeyed = new TestDoubleKeyedContainer(context) { Items = new Dictionary<double, TestItem> { [1.5] = new(context) } };

        // Act
        var objectResult = ResolveProperty(keyed, "ByAnything[7]");
        var doubleResult = ResolveProperty(doubleKeyed, "Items[2.5]");

        // Assert
        Assert.Equal((object)"7", objectResult?.Index);
        Assert.Equal((object)"2.5", doubleResult?.Index);
    }

    [Fact]
    public void WhenObjectKeyedDictionaryHasMixedKeys_ThenEveryWrittenPathResolvesBack()
    {
        // Arrange
        var context = CreateContext();
        var five = new TestItem(context) { Value = "five" };
        var text = new TestItem(context) { Value = "x" };
        var container = new TestKeyedContainer(context)
        {
            ByAnything = new Dictionary<object, TestItem> { [5] = five, ["x"] = text }
        };

        // Act
        var fivePath = GetValuePath(five)!;
        var textPath = GetValuePath(text)!;

        // Assert
        Assert.Equal("ByAnything[5].Value", fivePath);
        Assert.Equal("ByAnything[x].Value", textPath);
        Assert.Same(five, ResolveProperty(container, fivePath)?.Property.Subject);
        Assert.Same(text, ResolveProperty(container, textPath)?.Property.Subject);
        Assert.Equal((object)5, ResolveProperty(container, "ByAnything[5]")?.Index);
    }

    [Fact]
    public void WhenDoubleKeyIsWrittenUnderGermanCulture_ThenPathIsInvariantAndResolvesBack()
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var context = CreateContext();
            var item = new TestItem(context) { Value = "v" };
            var container = new TestDoubleKeyedContainer(context) { Items = new Dictionary<double, TestItem> { [1.5] = item } };

            // Act
            var path = GetValuePath(item);

            // Assert
            Assert.Equal("Items[1.5].Value", path);
            Assert.Same(item, ResolveProperty(container, path!)?.Property.Subject);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("Items[0]", true)]
    [InlineData("Items[2]", true)]
    [InlineData("Items[3]", false)]
    [InlineData("Items[-1]", false)]
    [InlineData("Items[01]", false)]
    [InlineData("Items[+1]", false)]
    public void WhenCollectionPositionIsResolved_ThenOnlyCanonicalPositionsInRangeResolve(string path, bool expectedResolved)
    {
        // Arrange
        var context = CreateContext();
        var container = new TestKeyedContainer(context) { Items = [new(context), new(context), new(context)] };

        // Act
        var subject = ResolveSubject(container, path);

        // Assert
        Assert.Equal(expectedResolved, subject is not null);
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
    public void WhenImmutableDictionaryHasIntKeys_ThenPresentKeyResolvesAndAbsentKeyDoesNot()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context);
        var container = new TestKeyedContainer(context)
        {
            ReadOnlyByNumber = ImmutableDictionary<int, TestItem>.Empty.Add(1, item)
        };

        // Act
        var present = ResolveSubject(container, "ReadOnlyByNumber[1]");
        var absent = ResolveSubject(container, "ReadOnlyByNumber[2]");

        // Assert
        Assert.Same(item, present);
        Assert.Null(absent);
    }

    [Theory]
    [InlineData("Sensor [Kitchen]", "Items['Sensor [Kitchen]'].Value")]
    [InlineData("'quoted", "Items['''quoted'].Value")]
    [InlineData("", "Items[''].Value")]
    [InlineData("Bob's", "Items[Bob's].Value")]
    [InlineData("a.b", "Items[a.b].Value")]
    [InlineData("a[b", "Items[a[b].Value")]
    public void WhenStringKeyIsWritten_ThenPathIsQuotedWhereNeededAndResolvesBack(string key, string expectedPath)
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestContainer(context) { Items = new Dictionary<string, TestItem> { [key] = item } };

        // Act
        var path = GetValuePath(item);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Same(item, ResolveProperty(container, path!)?.Property.Subject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenStringKeyIsLongerThan64Characters_ThenPathRoundTrips(bool objectKeyed)
    {
        // Arrange
        var key = new string('k', 70) + "]";
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        IInterceptorSubject container = objectKeyed
            ? new TestKeyedContainer(context) { ByAnything = new Dictionary<object, TestItem> { [key] = item } }
            : new TestContainer(context) { Items = new Dictionary<string, TestItem> { [key] = item } };

        // Act
        var path = GetValuePath(item);

        // Assert
        Assert.EndsWith($"['{key}'].Value", path);
        Assert.Same(item, ResolveProperty(container, path!)?.Property.Subject);
    }

    [Fact]
    public void WhenFormattableKeyTextIsLongerThan64Characters_ThenPathRoundTrips()
    {
        // Arrange
        var key = new LongTextKey(new string('k', 80));
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestKeyedContainer(context) { ByAnything = new Dictionary<object, TestItem> { [key] = item } };

        // Act
        var path = GetValuePath(item);

        // Assert
        Assert.Equal($"ByAnything[{key.Text}].Value", path);
        Assert.Same(item, ResolveProperty(container, path!)?.Property.Subject);
        Assert.Equal((object)key, ResolveProperty(container, $"ByAnything[{key.Text}]")?.Index);
    }

    public static IEnumerable<object[]> IntegerKeyExtremes()
    {
        yield return [nameof(TestIntegerKeyedContainer.BySByte), sbyte.MinValue];
        yield return [nameof(TestIntegerKeyedContainer.BySByte), sbyte.MaxValue];
        yield return [nameof(TestIntegerKeyedContainer.ByByte), byte.MinValue];
        yield return [nameof(TestIntegerKeyedContainer.ByByte), byte.MaxValue];
        yield return [nameof(TestIntegerKeyedContainer.ByInt16), short.MinValue];
        yield return [nameof(TestIntegerKeyedContainer.ByInt16), short.MaxValue];
        yield return [nameof(TestIntegerKeyedContainer.ByUInt16), ushort.MinValue];
        yield return [nameof(TestIntegerKeyedContainer.ByUInt16), ushort.MaxValue];
        yield return [nameof(TestIntegerKeyedContainer.ByUInt32), uint.MinValue];
        yield return [nameof(TestIntegerKeyedContainer.ByUInt32), uint.MaxValue];
        yield return [nameof(TestIntegerKeyedContainer.ByInt64), long.MinValue];
        yield return [nameof(TestIntegerKeyedContainer.ByInt64), long.MaxValue];
        yield return [nameof(TestIntegerKeyedContainer.ByUInt64), ulong.MinValue];
        yield return [nameof(TestIntegerKeyedContainer.ByUInt64), ulong.MaxValue];
    }

    [Theory]
    [MemberData(nameof(IntegerKeyExtremes))]
    public void WhenIntegerKeyIsAtTypeExtreme_ThenPathRoundTripsForThatWidth(string propertyName, object key)
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "v" };
        var container = new TestIntegerKeyedContainer(context);
        AssignIntegerKeyedDictionary(container, propertyName, key, item);

        // Act
        var path = GetValuePath(item);

        // Assert
        Assert.Equal($"{propertyName}[{Convert.ToString(key, CultureInfo.InvariantCulture)}].Value", path);
        Assert.Same(item, ResolveProperty(container, path!)?.Property.Subject);
    }

    [Theory]
    [InlineData("ByByte[256]")]
    [InlineData("ByUInt32[-1]")]
    public void WhenIntegerKeyIsOutOfRangeForItsWidth_ThenPathDoesNotResolve(string path)
    {
        // Arrange
        var context = CreateContext();
        var container = new TestIntegerKeyedContainer(context)
        {
            ByByte = new Dictionary<byte, TestItem> { [255] = new(context) },
            ByUInt32 = new Dictionary<uint, TestItem> { [0] = new(context) }
        };

        // Act
        var subject = ResolveSubject(container, path);

        // Assert
        Assert.Null(subject);
    }

    private static void AssignIntegerKeyedDictionary(TestIntegerKeyedContainer container, string propertyName, object key, TestItem item)
    {
        switch (propertyName)
        {
            case nameof(TestIntegerKeyedContainer.BySByte):
                container.BySByte = new Dictionary<sbyte, TestItem> { [(sbyte)key] = item };
                break;
            case nameof(TestIntegerKeyedContainer.ByByte):
                container.ByByte = new Dictionary<byte, TestItem> { [(byte)key] = item };
                break;
            case nameof(TestIntegerKeyedContainer.ByInt16):
                container.ByInt16 = new Dictionary<short, TestItem> { [(short)key] = item };
                break;
            case nameof(TestIntegerKeyedContainer.ByUInt16):
                container.ByUInt16 = new Dictionary<ushort, TestItem> { [(ushort)key] = item };
                break;
            case nameof(TestIntegerKeyedContainer.ByUInt32):
                container.ByUInt32 = new Dictionary<uint, TestItem> { [(uint)key] = item };
                break;
            case nameof(TestIntegerKeyedContainer.ByInt64):
                container.ByInt64 = new Dictionary<long, TestItem> { [(long)key] = item };
                break;
            case nameof(TestIntegerKeyedContainer.ByUInt64):
                container.ByUInt64 = new Dictionary<ulong, TestItem> { [(ulong)key] = item };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unknown integer-keyed property.");
        }
    }

    private readonly record struct LongTextKey(string Text) : ISpanFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => Text;

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            charsWritten = Text.Length <= destination.Length ? Text.Length : 0;
            return Text.AsSpan().TryCopyTo(destination);
        }
    }
}
