namespace Namotion.Interceptor.Generator.Tests;

public class VirtualPartialTests
{
    [Fact]
    public Task Test_VirtualPartial_GeneratesCorrectly()
    {
        // Arrange - Test that virtual + partial generates virtual property
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class BaseClass
{
    public virtual partial string VirtualProp { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert - Should generate virtual property implementation
        var generatedSource = generated.SingleSource();
        Assert.Contains("public virtual partial string VirtualProp", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public Task Test_OverridePartial_GeneratesCorrectly()
    {
        // Arrange - Test that override + partial generates override property
        const string source = @"
using Namotion.Interceptor.Attributes;

public partial class BaseClass
{
    public virtual string VirtualProp { get; set; }
}

[InterceptorSubject]
public partial class DerivedClass : BaseClass
{
    public override partial string VirtualProp { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert - Should generate override property implementation
        var generatedSource = generated.SingleSource();
        Assert.Contains("public override partial string VirtualProp", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public Task Test_VirtualInheritanceChain_GeneratesCorrectly()
    {
        // Arrange - Test full inheritance chain with virtual/override
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class BaseEntity
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Person : BaseEntity
{
    public override partial string Name { get; set; }
    public virtual partial int Age { get; set; }
}

[InterceptorSubject]
public partial class Employee : Person
{
    public override partial int Age { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert - Should generate all three classes correctly
        var generatedSource = generated.AllSources();
        Assert.Contains("public virtual partial string Name", generatedSource);
        Assert.Contains("public override partial string Name", generatedSource);
        Assert.Contains("public virtual partial int Age", generatedSource);
        Assert.Contains("public override partial int Age", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public void Test_ExplicitInterfacePartial_IsNotAllowedInCSharp()
    {
        // Arrange - Test if C# allows explicit interface + partial
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IHasName
{
    string Name { get; set; }
}

[InterceptorSubject]
public partial class ExplicitImpl : IHasName
{
    partial string IHasName.Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert - Should have compiler error
        Assert.NotEmpty(generated.CompilationErrors);
    }

    [Fact]
    public void Test_ImplicitInterfacePartial_Works()
    {
        // Arrange - Implicit interface implementation should work
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IHasName
{
    string Name { get; set; }
}

[InterceptorSubject]
public partial class ImplicitImpl : IHasName
{
    public partial string Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert - Should compile successfully
        Assert.NotEmpty(generated.Sources);
    }
}
