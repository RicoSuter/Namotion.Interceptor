using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathSegmentReaderTests
{
    [Fact]
    public void WhenIndexContainsPathSeparator_ThenReaderReadsThreeSegments()
    {
        // Arrange
        const string path = "car.radioStations[jazz108.5Fm].name";
        var reader = new PathSegmentReader(DefaultPathProvider.Instance, path);

        // Act
        var readFirst = reader.TryRead(out var first);
        var readSecond = reader.TryRead(out var second);
        var hasNextAfterSecond = reader.HasNext;
        var readThird = reader.TryRead(out var third);
        var hasNextAfterThird = reader.HasNext;
        var readFourth = reader.TryRead(out _);

        // Assert
        Assert.True(readFirst);
        Assert.Equal("car", first.Name.ToString());
        Assert.False(first.HasIndex);
        Assert.True(first.Index.IsEmpty);

        Assert.True(readSecond);
        Assert.Equal("radioStations", second.Name.ToString());
        Assert.True(second.HasIndex);
        Assert.Equal("jazz108.5Fm", second.Index.ToString());
        Assert.Equal("car.radioStations[jazz108.5Fm]".Length, second.End);
        Assert.True(hasNextAfterSecond);

        Assert.True(readThird);
        Assert.Equal("name", third.GetName());
        Assert.Equal(path.Length, third.End);
        Assert.False(hasNextAfterThird);

        Assert.False(readFourth);
        Assert.False(reader.IsMalformed);
        Assert.Null(reader.Error);
    }

    [Fact]
    public void WhenSegmentIsWholePath_ThenGetNameReturnsThePath()
    {
        // Arrange
        const string path = "name";
        var reader = new PathSegmentReader(DefaultPathProvider.Instance, path);

        // Act
        reader.TryRead(out var segment);

        // Assert
        Assert.Same(path, segment.GetName());
    }

    [Fact]
    public void WhenPathHasEmptySegmentsAndTrailingSeparator_ThenTheyAreSkippedAndLastSegmentIsKnown()
    {
        // Arrange
        var reader = new PathSegmentReader(new DefaultPathProvider('/'), "/a//b/");

        // Act
        var hasNextAtStart = reader.HasNext;
        reader.TryRead(out var first);
        var hasNextAfterFirst = reader.HasNext;
        reader.TryRead(out var second);
        var hasNextAfterSecond = reader.HasNext;
        var readThird = reader.TryRead(out _);

        // Assert
        Assert.True(hasNextAtStart);
        Assert.Equal("a", first.Name.ToString());
        Assert.True(hasNextAfterFirst);
        Assert.Equal("b", second.Name.ToString());
        Assert.False(hasNextAfterSecond);
        Assert.False(readThird);
        Assert.False(reader.IsMalformed);
    }

    [Theory]
    [InlineData("a[b")]
    [InlineData("a[b]]")]
    [InlineData("a[]")]
    [InlineData("[a]")]
    [InlineData("a.[b]")]
    [InlineData("a[1]b")]
    [InlineData("a[1][2]")]
    public void WhenPathIsMalformed_ThenReaderStopsAndReportsIt(string path)
    {
        // Arrange
        var pathProvider = DefaultPathProvider.Instance;
        var reader = new PathSegmentReader(pathProvider, path);
        pathProvider.TryParsePath(path, out _, out var expectedError);

        // Act
        while (reader.TryRead(out _))
        {
        }

        // Assert
        Assert.True(reader.IsMalformed);
        Assert.False(reader.HasNext);
        Assert.False(reader.TryRead(out _));
        Assert.Equal(expectedError, reader.Error);
    }

    [Fact]
    public void WhenReaderIsDefault_ThenItReadsNothing()
    {
        // Arrange
        var reader = default(PathSegmentReader);

        // Act
        var read = reader.TryRead(out var segment);

        // Assert
        Assert.False(read);
        Assert.False(reader.HasNext);
        Assert.False(reader.IsMalformed);
        Assert.Null(reader.Error);
        Assert.True(segment.Name.IsEmpty);
        Assert.False(segment.HasIndex);
    }

    [Fact]
    public void WhenResolvingSegmentsByHand_ThenEachSegmentResolvesOnItsSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var item = new TestItem(context) { Value = "x" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["a"] = item;

        var pathProvider = DefaultPathProvider.Instance;
        var reader = new PathSegmentReader(pathProvider, "Items[a].Value");

        // Act
        reader.TryRead(out var first);
        var resolvedFirst = pathProvider.TryResolvePathSegment(
            container.TryGetRegisteredSubject()!, first, out var firstProperty, out var firstKey, out var firstChild);

        reader.TryRead(out var second);
        var resolvedSecond = pathProvider.TryResolvePathSegment(
            firstChild!.TryGetRegisteredSubject()!, second, out var secondProperty, out var secondKey, out var secondChild);

        // Assert
        Assert.True(resolvedFirst);
        Assert.Equal(nameof(TestContainer.Items), firstProperty!.Name);
        Assert.Equal("a", firstKey);
        Assert.Same(item, firstChild);

        Assert.True(resolvedSecond);
        Assert.Equal(nameof(TestItem.Value), secondProperty!.Name);
        Assert.Null(secondKey);
        Assert.Null(secondChild);
        Assert.False(reader.HasNext);
    }

    [Fact]
    public void WhenResolvingDefaultSegment_ThenReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var container = new TestContainer(context) { Name = "Root" };

        // Act
        var resolved = DefaultPathProvider.Instance.TryResolvePathSegment(
            container.TryGetRegisteredSubject()!, default, out var property, out var key, out var child);

        // Assert
        Assert.False(resolved);
        Assert.Null(property);
        Assert.Null(key);
        Assert.Null(child);
    }
}
