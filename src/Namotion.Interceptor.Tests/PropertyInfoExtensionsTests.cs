using System;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests;

public partial class PropertyInfoExtensionsTests
{
    [AttributeUsage(AttributeTargets.Property)]
    private class UnitAttribute : Attribute
    {
        public string Unit { get; }
        public UnitAttribute(string unit) => Unit = unit;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    private class TagAttribute : Attribute
    {
        public string Value { get; }
        public TagAttribute(string value) => Value = value;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    private class SingleAttribute : Attribute
    {
        public string Value { get; }
        public SingleAttribute(string value) => Value = value;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    private class MultipleAttribute : Attribute
    {
        public string Value { get; }
        public MultipleAttribute(string value) => Value = value;
    }

    [AttributeUsage(AttributeTargets.Property)]
    private class DefaultAttribute : Attribute
    {
        public string Value { get; }
        public DefaultAttribute(string value) => Value = value;
    }

    private interface ITemperatureSensor
    {
        [Unit("°C")]
        double Temperature { get; }
    }

    [InterceptorSubject]
    private partial class TemperatureSensor : ITemperatureSensor
    {
        public partial double Temperature { get; set; }
    }

    private interface IWithMultipleAttributes
    {
        [Unit("m/s")]
        [Tag("speed")]
        double Speed { get; }
    }

    [InterceptorSubject]
    private partial class SpeedSensor : IWithMultipleAttributes
    {
        [Tag("class")]
        public partial double Speed { get; set; }
    }

    private interface IWithSingleAttribute
    {
        [Single("interface")]
        string Property { get; }
    }

    private interface IWithMultipleAttribute
    {
        [Multiple("interface")]
        string Property { get; }
    }

    private interface IFirst
    {
        [Single("first")]
        string Property { get; }
    }

    private interface ISecond
    {
        [Single("second")]
        string Property { get; }
    }

    private class ImplementsInterfaceOnly : IWithSingleAttribute
    {
        public string Property { get; set; } = "";
    }

    private class ImplementsInterfaceWithOwnAttribute : IWithSingleAttribute
    {
        [Single("class")]
        public string Property { get; set; } = "";
    }

    private class ImplementsInterfaceWithMultiple : IWithMultipleAttribute
    {
        [Multiple("class")]
        public string Property { get; set; } = "";
    }

    private class ImplementsMultipleInterfaces : IFirst, ISecond
    {
        public string Property { get; set; } = "";
    }

    private class ImplementsMultipleInterfacesWithOwn : IFirst, ISecond
    {
        [Single("class")]
        public string Property { get; set; } = "";
    }

    private class BaseClass
    {
        [Single("base")]
        public virtual string Property { get; set; } = "";
    }

    private class DerivedClass : BaseClass
    {
        public override string Property { get; set; } = "";
    }

    private class DerivedClassWithOwnAttribute : BaseClass
    {
        [Single("derived")]
        public override string Property { get; set; } = "";
    }

    private interface IWithIntProperty
    {
        [Single("int-interface")]
        int Value { get; }
    }

    private class HasStringPropertySameName : IWithIntProperty
    {
        public string Value { get; set; } = "";
        int IWithIntProperty.Value => 0;
    }

    private interface IParentInterface
    {
        [Single("parent-interface")]
        string Property { get; }
    }

    private interface IChildInterface : IParentInterface
    {
    }

    private interface IChildInterfaceWithOwn : IParentInterface
    {
        [Single("child-interface")]
        new string Property { get; }
    }

    private class ImplementsChildInterface : IChildInterface
    {
        public string Property { get; set; } = "";
    }

    private class ImplementsChildInterfaceWithOwn : IChildInterfaceWithOwn
    {
        public string Property { get; set; } = "";
    }

    private interface IWithDefault
    {
        [Default("interface")]
        string Property { get; }
    }

    private class BaseWithInterface : IWithDefault
    {
        [Default("base")]
        public virtual string Property { get; set; } = "";
    }

    private class DerivedFromBaseWithInterface : BaseWithInterface
    {
        public override string Property { get; set; } = "";
    }

    [Fact]
    public void InterfaceAttribute_AppearsOnImplementingClass()
    {
        // Arrange
        var property = typeof(ImplementsInterfaceOnly).GetProperty(nameof(ImplementsInterfaceOnly.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var single = Assert.Single(attributes.OfType<SingleAttribute>());
        Assert.Equal("interface", single.Value);
    }

    [Fact]
    public void ClassAttributeWins_WhenAllowMultipleFalse()
    {
        // Arrange
        var property = typeof(ImplementsInterfaceWithOwnAttribute).GetProperty(nameof(ImplementsInterfaceWithOwnAttribute.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var single = Assert.Single(attributes.OfType<SingleAttribute>());
        Assert.Equal("class", single.Value);
    }

    [Fact]
    public void BothIncluded_WhenAllowMultipleTrue()
    {
        // Arrange
        var property = typeof(ImplementsInterfaceWithMultiple).GetProperty(nameof(ImplementsInterfaceWithMultiple.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var multiples = attributes.OfType<MultipleAttribute>().ToList();
        Assert.Equal(2, multiples.Count);
        Assert.Contains(multiples, a => a.Value == "class");
        Assert.Contains(multiples, a => a.Value == "interface");
    }

    [Fact]
    public void MultipleInterfaces_FirstInterfaceWins_WhenAllowMultipleFalse()
    {
        // Arrange
        var property = typeof(ImplementsMultipleInterfaces).GetProperty(nameof(ImplementsMultipleInterfaces.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var single = Assert.Single(attributes.OfType<SingleAttribute>());
        Assert.True(single.Value == "first" || single.Value == "second");
    }

    [Fact]
    public void MultipleInterfaces_ClassWins_WhenClassHasAttribute()
    {
        // Arrange
        var property = typeof(ImplementsMultipleInterfacesWithOwn).GetProperty(nameof(ImplementsMultipleInterfacesWithOwn.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var single = Assert.Single(attributes.OfType<SingleAttribute>());
        Assert.Equal("class", single.Value);
    }

    [Fact]
    public void InterfaceAttribute_NotInherited_WhenPropertyTypeDiffers()
    {
        // Arrange
        var property = typeof(HasStringPropertySameName).GetProperty(nameof(HasStringPropertySameName.Value))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        Assert.Empty(attributes.OfType<SingleAttribute>());
    }

    [Fact]
    public void InterfaceInheritanceChain_ParentInterfaceAttribute_InheritedByImplementingClass()
    {
        // Arrange
        var property = typeof(ImplementsChildInterface).GetProperty(nameof(ImplementsChildInterface.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var single = Assert.Single(attributes.OfType<SingleAttribute>());
        Assert.Equal("parent-interface", single.Value);
    }

    [Fact]
    public void InterfaceInheritanceChain_ChildInterfaceAttribute_WinsOverParent()
    {
        // Arrange
        var property = typeof(ImplementsChildInterfaceWithOwn).GetProperty(nameof(ImplementsChildInterfaceWithOwn.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var singles = attributes.OfType<SingleAttribute>().ToList();
        Assert.Single(singles);
        Assert.True(singles[0].Value == "child-interface" || singles[0].Value == "parent-interface");
    }

    [Fact]
    public void BaseClassAttribute_InheritedByDerivedClass()
    {
        // Arrange
        var property = typeof(DerivedClass).GetProperty(nameof(DerivedClass.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var single = Assert.Single(attributes.OfType<SingleAttribute>());
        Assert.Equal("base", single.Value);
    }

    [Fact]
    public void DerivedClassAttribute_WinsOverBase_WhenAllowMultipleFalse()
    {
        // Arrange
        var property = typeof(DerivedClassWithOwnAttribute).GetProperty(nameof(DerivedClassWithOwnAttribute.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var singles = attributes.OfType<SingleAttribute>().ToList();
        Assert.Contains(singles, s => s.Value == "derived");
    }

    [Fact]
    public void CombinedInheritance_ClassAndInterface_CorrectOrder()
    {
        // Arrange
        var property = typeof(DerivedFromBaseWithInterface).GetProperty(nameof(DerivedFromBaseWithInterface.Property))!;

        // Act
        var attributes = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        var defaults = attributes.OfType<DefaultAttribute>().ToList();
        Assert.Single(defaults);
        Assert.Equal("base", defaults[0].Value);
    }

    [Fact]
    public void Cache_ReturnsSameInstance()
    {
        // Arrange
        var property = typeof(ImplementsInterfaceOnly).GetProperty(nameof(ImplementsInterfaceOnly.Property))!;

        // Act
        var first = property.GetCustomAttributesIncludingInterfaces();
        var second = property.GetCustomAttributesIncludingInterfaces();

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void InterfaceAttribute_FlowsToSubjectPropertyMetadata()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var sensor = new TemperatureSensor(context);

        // Act
        var properties = ((IInterceptorSubject)sensor).Properties;
        var tempProperty = properties["Temperature"];

        // Assert
        var unitAttribute = tempProperty.Attributes.OfType<UnitAttribute>().SingleOrDefault();
        Assert.NotNull(unitAttribute);
        Assert.Equal("°C", unitAttribute.Unit);
    }

    [Fact]
    public void MultipleInterfaceAttributes_FlowToSubjectPropertyMetadata()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var sensor = new SpeedSensor(context);

        // Act
        var properties = ((IInterceptorSubject)sensor).Properties;
        var speedProperty = properties["Speed"];

        // Assert
        var unitAttribute = speedProperty.Attributes.OfType<UnitAttribute>().SingleOrDefault();
        Assert.NotNull(unitAttribute);
        Assert.Equal("m/s", unitAttribute.Unit);

        var tagAttributes = speedProperty.Attributes.OfType<TagAttribute>().ToList();
        Assert.Equal(2, tagAttributes.Count);
        Assert.Contains(tagAttributes, a => a.Value == "class");
        Assert.Contains(tagAttributes, a => a.Value == "speed");
    }
}
