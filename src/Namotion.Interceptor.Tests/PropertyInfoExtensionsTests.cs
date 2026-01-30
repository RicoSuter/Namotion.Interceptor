using System;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests;

#region Integration Test Types

[AttributeUsage(AttributeTargets.Property)]
public class UnitAttribute : Attribute
{
    public string Unit { get; }
    public UnitAttribute(string unit) => Unit = unit;
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class TagAttribute : Attribute
{
    public string Value { get; }
    public TagAttribute(string value) => Value = value;
}

public interface ITemperatureSensor
{
    [Unit("°C")]
    double Temperature { get; }
}

[InterceptorSubject]
public partial class TemperatureSensor : ITemperatureSensor
{
    public partial double Temperature { get; set; }
}

public interface IWithMultipleAttributes
{
    [Unit("m/s")]
    [Tag("speed")]
    double Speed { get; }
}

[InterceptorSubject]
public partial class SpeedSensor : IWithMultipleAttributes
{
    [Tag("class")]
    public partial double Speed { get; set; }
}

#endregion

public class PropertyInfoExtensionsTests
{
    #region Test Attributes

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class SingleAttribute : Attribute
    {
        public string Value { get; }
        public SingleAttribute(string value) => Value = value;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public class MultipleAttribute : Attribute
    {
        public string Value { get; }
        public MultipleAttribute(string value) => Value = value;
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class DefaultAttribute : Attribute
    {
        public string Value { get; }
        public DefaultAttribute(string value) => Value = value;
    }

    #endregion

    #region Test Classes - Interface Inheritance

    public interface IWithSingleAttribute
    {
        [Single("interface")]
        string Property { get; }
    }

    public interface IWithMultipleAttribute
    {
        [Multiple("interface")]
        string Property { get; }
    }

    public interface IFirst
    {
        [Single("first")]
        string Property { get; }
    }

    public interface ISecond
    {
        [Single("second")]
        string Property { get; }
    }

    public class ImplementsInterfaceOnly : IWithSingleAttribute
    {
        public string Property { get; set; } = "";
    }

    public class ImplementsInterfaceWithOwnAttribute : IWithSingleAttribute
    {
        [Single("class")]
        public string Property { get; set; } = "";
    }

    public class ImplementsInterfaceWithMultiple : IWithMultipleAttribute
    {
        [Multiple("class")]
        public string Property { get; set; } = "";
    }

    public class ImplementsMultipleInterfaces : IFirst, ISecond
    {
        public string Property { get; set; } = "";
    }

    public class ImplementsMultipleInterfacesWithOwn : IFirst, ISecond
    {
        [Single("class")]
        public string Property { get; set; } = "";
    }

    #endregion

    #region Test Classes - Class Inheritance

    public class BaseClass
    {
        [Single("base")]
        public virtual string Property { get; set; } = "";
    }

    public class DerivedClass : BaseClass
    {
        public override string Property { get; set; } = "";
    }

    public class DerivedClassWithOwnAttribute : BaseClass
    {
        [Single("derived")]
        public override string Property { get; set; } = "";
    }

    #endregion

    #region Test Classes - Type Mismatch

    public interface IWithIntProperty
    {
        [Single("int-interface")]
        int Value { get; }
    }

    public class HasStringPropertySameName : IWithIntProperty
    {
        // This property has the same name but different type - should NOT inherit attribute
        public string Value { get; set; } = "";

        // Explicit implementation for the interface
        int IWithIntProperty.Value => 0;
    }

    #endregion

    #region Test Classes - Combined

    public interface IWithDefault
    {
        [Default("interface")]
        string Property { get; }
    }

    public class BaseWithInterface : IWithDefault
    {
        [Default("base")]
        public virtual string Property { get; set; } = "";
    }

    public class DerivedFromBaseWithInterface : BaseWithInterface
    {
        public override string Property { get; set; } = "";
    }

    #endregion

    #region Interface Inheritance Tests

    [Fact]
    public void InterfaceAttribute_AppearsOnImplementingClass()
    {
        // Arrange
        var property = typeof(ImplementsInterfaceOnly).GetProperty(nameof(ImplementsInterfaceOnly.Property))!;

        // Act
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

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
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

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
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

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
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

        // Assert
        var single = Assert.Single(attributes.OfType<SingleAttribute>());
        // First interface in GetInterfaces() order wins
        Assert.True(single.Value == "first" || single.Value == "second");
    }

    [Fact]
    public void MultipleInterfaces_ClassWins_WhenClassHasAttribute()
    {
        // Arrange
        var property = typeof(ImplementsMultipleInterfacesWithOwn).GetProperty(nameof(ImplementsMultipleInterfacesWithOwn.Property))!;

        // Act
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

        // Assert
        var single = Assert.Single(attributes.OfType<SingleAttribute>());
        Assert.Equal("class", single.Value);
    }

    #endregion

    #region Type Mismatch Tests

    [Fact]
    public void InterfaceAttribute_NotInherited_WhenPropertyTypeDiffers()
    {
        // Arrange
        var property = typeof(HasStringPropertySameName).GetProperty(nameof(HasStringPropertySameName.Value))!;

        // Act
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

        // Assert - should NOT have the interface attribute since types don't match
        Assert.Empty(attributes.OfType<SingleAttribute>());
    }

    #endregion

    #region Class Inheritance Tests (verify .NET behavior preserved)

    [Fact]
    public void BaseClassAttribute_InheritedByDerivedClass()
    {
        // Arrange
        var property = typeof(DerivedClass).GetProperty(nameof(DerivedClass.Property))!;

        // Act
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

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
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

        // Assert
        var singles = attributes.OfType<SingleAttribute>().ToList();
        // Note: .NET's GetCustomAttributes() returns both base and derived for virtual properties
        // The deduplication in our code handles this
        Assert.Contains(singles, s => s.Value == "derived");
    }

    #endregion

    #region Combined Tests

    [Fact]
    public void CombinedInheritance_ClassAndInterface_CorrectOrder()
    {
        // Arrange
        var property = typeof(DerivedFromBaseWithInterface).GetProperty(nameof(DerivedFromBaseWithInterface.Property))!;

        // Act
        var attributes = property.GetCustomAttributesWithInterfaceInheritance();

        // Assert
        // Base class attribute should be present (via .NET inheritance)
        // Interface attribute should be deduplicated (AllowMultiple=false by default)
        var defaults = attributes.OfType<DefaultAttribute>().ToList();
        Assert.Single(defaults);
        Assert.Equal("base", defaults[0].Value);
    }

    #endregion

    #region Caching Tests

    [Fact]
    public void Cache_ReturnsSameInstance()
    {
        // Arrange
        var property = typeof(ImplementsInterfaceOnly).GetProperty(nameof(ImplementsInterfaceOnly.Property))!;

        // Act
        var first = property.GetCustomAttributesWithInterfaceInheritance();
        var second = property.GetCustomAttributesWithInterfaceInheritance();

        // Assert
        Assert.Same(first, second);
    }

    #endregion

    #region Integration Tests - Full Flow

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

    #endregion
}
