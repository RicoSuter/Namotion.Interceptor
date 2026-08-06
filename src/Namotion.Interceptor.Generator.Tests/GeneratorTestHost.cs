using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// The outcome of running the generator over a single source snippet.
/// </summary>
internal sealed record GeneratorRunResult(
    IReadOnlyList<GeneratedSourceResult> Sources,
    IReadOnlyList<Diagnostic> GeneratorDiagnostics,
    IReadOnlyList<Diagnostic> CompilationErrors)
{
    public string SingleSource() => Sources.Single().SourceText.ToString();

    public string AllSources() => string.Join("\n\n", Sources.Select(s => s.SourceText));
}

/// <summary>
/// Runs the incremental generator against an in-memory compilation.
/// </summary>
internal static class GeneratorTestHost
{
    private static readonly IReadOnlyList<MetadataReference> References = CreateReferences();

    public static GeneratorRunResult Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new InterceptorSubjectGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var runResult = driver.GetRunResult();

        return new GeneratorRunResult(
            runResult.Results.SelectMany(result => result.GeneratedSources).ToList(),
            runResult.Diagnostics.ToList(),
            outputCompilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToList());
    }

    /// <summary>
    /// The trusted platform assembly set is used instead of the loaded assemblies, because
    /// System.Text.Json is not loaded in the test AppDomain and the generated code needs
    /// JsonIgnore to resolve.
    /// </summary>
    private static IReadOnlyList<MetadataReference> CreateReferences()
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path));

        var loadedAssemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .Select(assembly => assembly.Location);

        return trustedAssemblies
            .Concat(loadedAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }
}
