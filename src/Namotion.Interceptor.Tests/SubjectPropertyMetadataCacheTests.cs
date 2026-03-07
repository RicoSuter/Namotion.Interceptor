using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests;

public class SubjectPropertyMetadataCacheTests
{
    [Fact]
    public void Get_ReturnsOnlyInterfaceProperties_ForTypeWithNoUserDefinedProperties()
    {
        // Act
        var result = SubjectPropertyMetadataCache.Get<EmptySubject>();

        // Assert
        Assert.NotNull(result);
        // EmptySubject has no user-defined properties, but the generated code
        // implements IInterceptorSubject which has explicit interface properties
        // (Context, Data, Properties, SyncRoot) that are discovered
        Assert.All(result.Keys, key => Assert.StartsWith("Namotion.Interceptor.IInterceptorSubject.", key));
    }

    [Fact]
    public void Get_DiscoversPropertiesFromType()
    {
        // Act
        var result = SubjectPropertyMetadataCache.Get<SimpleSubject>();

        // Assert
        Assert.Contains("Name", result.Keys);
        Assert.Contains("Value", result.Keys);
    }

    [Fact]
    public void Get_DiscoversPropertiesFromBaseClass()
    {
        // Act
        var result = SubjectPropertyMetadataCache.Get<DerivedSubject>();

        // Assert
        Assert.Contains("BaseName", result.Keys);  // From base
        Assert.Contains("DerivedValue", result.Keys);  // From derived
    }

    [Fact]
    public void Get_MostDerivedPropertyWins_ForOverrides()
    {
        // Act
        var result = SubjectPropertyMetadataCache.Get<OverridingSubject>();

        // Assert - should have only one "Name" property from most derived
        var nameCount = result.Keys.Count(k => k == "Name");
        Assert.Equal(1, nameCount);
    }

    [Fact]
    public void Get_DiscoversInterfaceDefaultImplementations()
    {
        // Act
        var result = SubjectPropertyMetadataCache.Get<SubjectWithInterfaceDefault>();

        // Assert
        Assert.Contains("Value", result.Keys);  // Class property
        Assert.Contains("ComputedValue", result.Keys);  // Interface default
    }

    [Fact]
    public void Get_MostDerivedInterfaceWins_ForShadowedProperties()
    {
        // Act
        var result = SubjectPropertyMetadataCache.Get<SubjectWithShadowedInterface>();

        // Assert - should have "Status" from the most derived interface
        Assert.Contains("Status", result.Keys);
        var statusMetadata = result["Status"];
        // The getter should return "Derived" not "Base"
        // This verifies that GetInterfacesSortedByDepth correctly orders interfaces
        // so that the most derived interface's property is discovered first
        Assert.NotNull(statusMetadata.PropertyInfo);
        Assert.Equal(typeof(IDerivedStatus), statusMetadata.PropertyInfo.DeclaringType);
    }

    [Fact]
    public void Get_ReturnsSameInstance_ForSameType()
    {
        // Act
        var result1 = SubjectPropertyMetadataCache.Get<SimpleSubject>();
        var result2 = SubjectPropertyMetadataCache.Get<SimpleSubject>();

        // Assert
        Assert.Same(result1, result2);
    }

    [Fact]
    public void Get_ReturnsDifferentInstances_ForDifferentTypes()
    {
        // Act
        var result1 = SubjectPropertyMetadataCache.Get<SimpleSubject>();
        var result2 = SubjectPropertyMetadataCache.Get<EmptySubject>();

        // Assert
        Assert.NotSame(result1, result2);
    }
}

[InterceptorSubject]
public partial class EmptySubject
{
}

[InterceptorSubject]
public partial class SimpleSubject
{
    public partial string Name { get; set; }
    public partial int Value { get; set; }
}

[InterceptorSubject]
public partial class BaseSubject
{
    public partial string BaseName { get; set; }
}

[InterceptorSubject]
public partial class DerivedSubject : BaseSubject
{
    public partial int DerivedValue { get; set; }
}

[InterceptorSubject]
public partial class SubjectWithVirtualProperty
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class OverridingSubject : SubjectWithVirtualProperty
{
    public override partial string Name { get; set; }
}

public interface IHasComputedValue
{
    double Value { get; }
    double ComputedValue => Value * 2;
}

[InterceptorSubject]
public partial class SubjectWithInterfaceDefault : IHasComputedValue
{
    public partial double Value { get; set; }
}

public interface IBaseStatus
{
    string Status => "Base";
}

public interface IDerivedStatus : IBaseStatus
{
    new string Status => "Derived";
}

[InterceptorSubject]
public partial class SubjectWithShadowedInterface : IDerivedStatus
{
    public partial int Value { get; set; }
}
