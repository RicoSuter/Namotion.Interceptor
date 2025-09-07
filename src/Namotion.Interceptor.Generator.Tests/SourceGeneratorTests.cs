using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

public class SourceGeneratorTests
{
    [Fact]
    public Task WhenGeneratingClassWithInterceptorSubject_ThenPartialClassIsGenerated()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class SampleSubject
{{
    public partial int Value {{ get; set; }}
    public partial string? Name {{ get; set; }}
}}";

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
            .SelectMany(r => r.GeneratedSources)
            .FirstOrDefault(s => s.HintName == "SampleSubject.g.cs");

        var generatedSource = generated.SourceText.ToString();
        return Verify(generatedSource);
    }
}
