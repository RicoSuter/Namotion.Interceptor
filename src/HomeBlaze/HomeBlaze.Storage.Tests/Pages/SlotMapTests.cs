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

    [Fact]
    public void WhenDerivedTypeOverridesSlotProperty_ThenTheSlotIsFoundOnce()
    {
        // Act
        var map = SlotMap.For(typeof(TestSlotDerived));

        // Assert
        Assert.Null(map.Error);
        var slot = Assert.Single(map.Named.Values);
        Assert.Equal("Content", slot.Name);
        Assert.Same(slot, map.Default);
    }

    [Fact]
    public void WhenSlotNameHasLettersDigitsAndUnderscores_ThenItIsValid()
    {
        // Act
        var map = SlotMap.For(typeof(NameWithUnderscoreAndDigit));

        // Assert
        Assert.Null(map.Error);
        Assert.Equal(["_Item_2"], map.Named.Keys);
    }

    [Theory]
    [InlineData(typeof(TwoDefaults), "more than one default slot")]
    [InlineData(typeof(TypedDefault), "default slot 'Content' must be of type IInterceptorSubject")]
    [InlineData(typeof(ArrayDefault), "default slot 'Content' must be of type IInterceptorSubject")]
    [InlineData(typeof(DottedName), "'A.B' must not contain a dot")]
    [InlineData(typeof(HyphenatedName), "the slot name 'My-Item' cannot be written on a page. A slot name starts with a letter or '_' and has only letters, digits and '_'.")]
    [InlineData(typeof(NameWithLeadingDigit), "the slot name '1st' cannot be written on a page.")]
    [InlineData(typeof(NameWithSpace), "the slot name 'My Item' cannot be written on a page.")]
    [InlineData(typeof(DuplicateNames), "'Item' is declared twice")]
    [InlineData(typeof(EmptyName), "the slot of the property 'Content' needs a name")]
    [InlineData(typeof(WhitespaceName), "the slot of the property 'Content' needs a name")]
    [InlineData(typeof(StringSlot), "the slot 'Content' must hold subjects")]
    [InlineData(typeof(InterfaceSlot), "the type ITestSlotValue of the slot 'Content' cannot be created")]
    [InlineData(typeof(AbstractSlot), "the type TestAbstractRow of the slot 'Row' cannot be created")]
    [InlineData(typeof(NotInterceptedSlot), "the slot 'Content' must be a partial property of a subject class")]
    [InlineData(typeof(PlainSlotHost), "the slot 'Content' must be a partial property of a subject class")]
    [InlineData(typeof(NoSetterSlot), "the slot 'Content' needs a setter")]
    public void WhenDeclarationIsInvalid_ThenMapCarriesAnError(Type type, string expectedPart)
    {
        // Act
        var map = SlotMap.For(type);

        // Assert
        Assert.Contains(expectedPart, map.Error);
    }
}
