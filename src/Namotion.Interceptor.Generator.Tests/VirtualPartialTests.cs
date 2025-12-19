using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
        var generated = GenerateCode(source);

        // Assert - Should generate virtual property implementation
        var generatedSource = generated.Single().SourceText.ToString();
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
        var generated = GenerateCode(source);

        // Assert - Should generate override property implementation
        var generatedSource = generated.Single().SourceText.ToString();
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
        var generated = GenerateCode(source);

        // Assert - Should generate all three classes correctly
        var generatedSource = string.Join("\n\n", generated.Select(g => g.SourceText.ToString()));
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
        var compilation = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();

        // Assert - Should have compiler error
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(errors);
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
        var generated = GenerateCode(source);

        // Assert - Should compile successfully
        Assert.NotEmpty(generated);
    }

    private static Compilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList();

        return CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IEnumerable<Microsoft.CodeAnalysis.GeneratedSourceResult> GenerateCode(string source)
    {
        var compilation = CreateCompilation(source);
        var generator = new InterceptorSubjectGenerator();
        
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();
        return runResult.Results.SelectMany(r => r.GeneratedSources);
    }
}
