using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// The outcome of running the generator over a single source snippet.
/// </summary>
internal sealed record GeneratorRunResult(
    IReadOnlyList<GeneratedSourceResult> Sources,
    IReadOnlyList<Diagnostic> GeneratorDiagnostics,
    IReadOnlyList<Diagnostic> CompilationDiagnostics)
{
    /// <summary>
    /// Warnings are kept alongside the errors because a generated shape can compile and still be
    /// wrong: a shadowing property that omits 'new' produces CS0108, which never surfaces here as
    /// an error.
    /// </summary>
    public IReadOnlyList<Diagnostic> CompilationErrors { get; } = CompilationDiagnostics
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        .ToList();

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
        return RunCore(source, References);
    }

    /// <summary>
    /// Runs the generator against <paramref name="mainSource"/> in a compilation that additionally
    /// references a separate assembly compiled from <paramref name="librarySource"/>. Use this to
    /// verify accessibility rules that only manifest across an assembly boundary (e.g. an
    /// <c>internal</c> or <c>protected internal</c> interface default member declared in a
    /// referenced assembly, where the generated code's own assembly has no InternalsVisibleTo).
    /// </summary>
    public static GeneratorRunResult RunWithLibraryReference(string librarySource, string mainSource)
    {
        var libraryCompilation = CSharpCompilation.Create(
            assemblyName: "TestLibrary",
            syntaxTrees: [CSharpSyntaxTree.ParseText(librarySource)],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var libraryStream = new MemoryStream();
        var emitResult = libraryCompilation.Emit(libraryStream);
        Assert.True(
            emitResult.Success,
            "Library compilation did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, emitResult.Diagnostics.Select(d => d.ToString())));

        libraryStream.Position = 0;
        var libraryReference = MetadataReference.CreateFromStream(libraryStream);

        return RunCore(mainSource, References.Append(libraryReference).ToList());
    }

    private static GeneratorRunResult RunCore(string source, IReadOnlyList<MetadataReference> references)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new InterceptorSubjectGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var runResult = driver.GetRunResult();

        return new GeneratorRunResult(
            runResult.Results.SelectMany(result => result.GeneratedSources).ToList(),
            runResult.Diagnostics.ToList(),
            outputCompilation.GetDiagnostics().ToList());
    }

    /// <summary>
    /// Runs the generator and fails the test if the resulting compilation has any error.
    /// Use for inputs that are themselves valid C#. Inputs that are invalid by construction
    /// (CS0754, CS0592) must use <see cref="Run"/> and assert on the expected error instead.
    /// </summary>
    public static GeneratorRunResult RunExpectingCleanCompilation(string source)
    {
        var result = Run(source);

        Assert.True(
            result.CompilationErrors.Count == 0,
            "Generated code did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationErrors.Select(d => d.ToString())));

        return result;
    }

    /// <summary>
    /// Same contract as <see cref="RunExpectingCleanCompilation"/>, but for
    /// <see cref="RunWithLibraryReference"/>.
    /// </summary>
    public static GeneratorRunResult RunWithLibraryReferenceExpectingCleanCompilation(string librarySource, string mainSource)
    {
        var result = RunWithLibraryReference(librarySource, mainSource);

        Assert.True(
            result.CompilationErrors.Count == 0,
            "Generated code did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationErrors.Select(d => d.ToString())));

        return result;
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
