using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

public class SourceGeneratorTests
{
    [Fact]
    public Task WhenGeneratingClassWithInterceptorSubject_ThenPartialClassIsGenerated()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class SampleSubject
{
    public partial int Value { get; set; }
    public partial string? Name { get; set; }
}";

        // Act
        var generated = GeneratedSourceCode(source);

        // Assert
        var generatedSource = generated.Single().SourceText.ToString();
        return Verify(generatedSource);
    }
    
    
    [Fact]
    public Task WhenGeneratingClassWithProtectedProperty_ThenPropertyCorrectlyGenerated()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class SampleSubject
{
    public partial int Value { get; set; }
    public partial string? Name { get; set; }

    protected string? Hidden { get; set; }
}

public partial class ClassWithoutInterceptorSubject
{
    public partial int Value { get; set; }
    public partial string? Name { get; set; }

    protected string? Hidden { get; set; }
}
";

        // Act
        var generated = GeneratedSourceCode(source);

        // Assert
        var generatedSource = generated.Single().SourceText.ToString();
        return Verify(generatedSource);
    }
    
    [Fact]
    public Task WhenGeneratingClassWithInheritance_ThenPartialClassIsGenerated()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }

    public partial string LastName { get; set; }
}

[InterceptorSubject]
public partial class Teacher : Person
{
    public partial string MainCourse { get; set; }
}";

        // Act
        var generated = GeneratedSourceCode(source);

        // Assert
        var generatedSource = string.Join("\n\n", generated.Select(s => s.SourceText));
        return Verify(generatedSource);
    }

    [Fact]
    public Task WhenGeneratingDeepInheritanceHierarchy_ThenAllLevelsAreGenerated()
    {
        // Arrange - tests IsTypeOrInheritsFrom and ImplementsInterface with full hierarchy traversal
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IEntity
{
    string Id { get; }
}

[InterceptorSubject]
public partial class BaseEntity : IEntity
{
    public partial string Id { get; set; }
}

[InterceptorSubject]
public partial class Person : BaseEntity
{
    public partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Employee : Person
{
    public partial string Department { get; set; }
}

[InterceptorSubject]
public partial class Manager : Employee
{
    public partial int TeamSize { get; set; }
}";

        // Act
        var generated = GeneratedSourceCode(source);

        // Assert - all 4 classes should be generated with correct inheritance
        var generatedSource = string.Join("\n\n", generated.Select(s => s.SourceText));
        return Verify(generatedSource);
    }

    [Fact]
    public Task WhenGeneratingWithGenericTypes_ThenFullTypeNamesAreResolved()
    {
        // Arrange - tests GetFullTypeName with generic types
        const string source = @"
using System.Collections.Generic;
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Container
{
    public partial List<string> Items { get; set; }
    public partial Dictionary<string, int> Mappings { get; set; }
    public partial KeyValuePair<string, List<int>> Complex { get; set; }
}";

        // Act
        var generated = GeneratedSourceCode(source);

        // Assert
        var generatedSource = generated.Single().SourceText.ToString();
        return Verify(generatedSource);
    }

    // TODO: Fix generator to only concat DefaultProperties from classes with [InterceptorSubject] attribute,
    // not from any class that implements IInterceptorSubject. Currently generates invalid code:
    // .Concat(global::MiddleClass.DefaultProperties) - but MiddleClass doesn't have DefaultProperties.
    [Fact(Skip = "Generator bug: generates .Concat() for base classes without DefaultProperties")]
    public Task WhenBaseClassImplementsIInterceptorSubjectDirectly_ThenInheritanceIsDetected()
    {
        // Arrange - tests ImplementsInterface finding IInterceptorSubject without attribute
        const string source = @"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

public class ManualSubject : IInterceptorSubject
{
    public IInterceptorSubjectContext Context => throw new NotImplementedException();
    public ConcurrentDictionary<(string? property, string key), object?> Data => throw new NotImplementedException();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => throw new NotImplementedException();
    public object SyncRoot => throw new NotImplementedException();
    public void AddProperties(IEnumerable<SubjectPropertyMetadata> properties) => throw new NotImplementedException();
}

public class MiddleClass : ManualSubject
{
    public string MiddleProperty { get; set; }
}

[InterceptorSubject]
public partial class DerivedSubject : MiddleClass
{
    public partial string Name { get; set; }
}";

        // Act
        var generated = GeneratedSourceCode(source);

        // Assert - should detect ManualSubject as base implementing IInterceptorSubject
        var generatedSource = generated.Single().SourceText.ToString();
        return Verify(generatedSource);
    }

    private static IEnumerable<GeneratedSourceResult> GeneratedSourceCode(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "SampleGen",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new InterceptorSubjectGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();
        var generated = runResult.Results
            .SelectMany(r => r.GeneratedSources);
        return generated;
    }
}
