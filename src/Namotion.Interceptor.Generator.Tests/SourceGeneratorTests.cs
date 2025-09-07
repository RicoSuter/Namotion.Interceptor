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
{{
    public partial int Value {{ get; set; }}
    public partial string? Name {{ get; set; }}
}}";

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
{{
    public partial string FirstName { get; set; }

    public partial string LastName { get; set; }
}}

[InterceptorSubject]
public partial class Teacher : Person
{{
    public partial string MainCourse { get; set; }
}}";

        // Act
        var generated = GeneratedSourceCode(source);

        // Assert
        var generatedSource = string.Join("\n\n", generated.Select(s => s.SourceText));
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
