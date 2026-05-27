using Namotion.Interceptor.Connectors.Mapping;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class PropertyMappingTests
{
    private sealed record TestMapping(string? A, int? B) : IPropertyMapping<TestMapping>
    {
        public static TestMapping Merge(TestMapping primary, TestMapping fallback) => new(
            A: primary.A ?? fallback.A,
            B: primary.B ?? fallback.B);
    }

    [Fact]
    public void WhenMergingPartialMappings_ThenPrimaryPrecedesFallback()
    {
        // Arrange
        var fallback = new TestMapping(A: "fallback", B: 1);
        var primary = new TestMapping(A: "primary", B: null);

        // Act
        var merged = TestMapping.Merge(primary, fallback);

        // Assert
        Assert.Equal("primary", merged.A);
        Assert.Equal(1, merged.B);
    }
}
