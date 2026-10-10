using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

public class SlotMapTests
{
    [Fact]
    public void WhenTypeDeclaresSlots_ThenMapDescribesEachOne()
    {
        // Act
        var map = SlotMap.For(typeof(TestPanel));

        // Assert
        Assert.Null(map.Error);
        Assert.Equal("Content", map.Default!.Name);
        Assert.Equal(["Content", "Footer", "Item", "Row"], map.Named.Keys.Order());

        var row = map.Named["Row"];
        Assert.True(row.IsList);
        Assert.False(row.IsOpen);
        Assert.Equal(typeof(TestRow), row.ValueType);

        var item = map.Named["Item"];
        Assert.True(item.IsList);
        Assert.True(item.IsOpen);
        Assert.Equal(typeof(IInterceptorSubject), item.ValueType);

        Assert.False(map.Named["Footer"].IsList);
    }

    [Fact]
    public void WhenTypeHasNoSlots_ThenMapIsEmpty()
    {
        // Act
        var map = SlotMap.For(typeof(TestLeaf));

        // Assert
        Assert.Null(map.Error);
        Assert.Null(map.Default);
        Assert.Empty(map.Named);
    }

    [Theory]
    [InlineData(typeof(TwoDefaults), "more than one default slot")]
    [InlineData(typeof(TypedDefault), "default slot 'Content' must be of type IInterceptorSubject")]
    [InlineData(typeof(DottedName), "'A.B' must not contain a dot")]
    [InlineData(typeof(DuplicateNames), "'Item' is declared twice")]
    public void WhenDeclarationIsInvalid_ThenMapCarriesAnError(Type type, string expectedPart)
    {
        // Act
        var map = SlotMap.For(type);

        // Assert
        Assert.Contains(expectedPart, map.Error);
    }
}
