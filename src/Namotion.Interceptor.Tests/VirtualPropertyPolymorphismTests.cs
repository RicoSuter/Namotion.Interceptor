using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests;

public class VirtualPropertyPolymorphismTests
{
    [Fact]
    public void OverrideProperty_AccessedViaBase_ReturnsSameValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var derived = new PolymorphicDerived(context) { Name = "Test" };

        // Act - Access via base reference
        PolymorphicBase baseRef = derived;
        
        // Assert - Should be the same value (polymorphic behavior)
        Assert.Equal("Test", derived.Name);
        Assert.Equal("Test", baseRef.Name);
        Assert.Equal(derived.Name, baseRef.Name);
    }
    
    [Fact]
    public void OverrideProperty_SetViaBase_UpdatesDerivedValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var derived = new PolymorphicDerived(context);
        PolymorphicBase baseRef = derived;

        // Act - Set via base reference
        baseRef.Name = "SetViaBase";
        
        // Assert - Derived sees the change
        Assert.Equal("SetViaBase", derived.Name);
    }
}

[InterceptorSubject]
public partial class PolymorphicBase
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class PolymorphicDerived : PolymorphicBase
{
    public override partial string Name { get; set; }
}
