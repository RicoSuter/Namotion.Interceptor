using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Pins the raise classification for base subjects that are only available as metadata (compiled
/// in another assembly). There the generated interface list is visible, so the extractor relies on
/// the GeneratedCode marker on the emitted RaisePropertyChanged: marker present means a wrapped
/// (local-origin) raise the child can call bare; no marker means a hand-written or pre-marker
/// raise the child must wrap at its call sites.
/// </summary>
public class MetadataBaseClassificationTests
{
    private const string WrappedRaiseCallSite =
        """
                            using (SubjectChangeContext.WithLocalOrigin())
                            {
                                RaisePropertyChanged(nameof(ChildName));
                            }
        """;

    private const string BareRaiseCallSite =
        "                    RaisePropertyChanged(nameof(ChildName));";

    [Fact]
    public void WhenMetadataBaseIsOrdinaryGeneratedSubject_ThenChildCallsRaiseBare()
    {
        // Arrange: a plain generated subject compiled to metadata; its generated raise carries the
        // GeneratedCode marker and already enters the local-origin scope.
        const string baseSource = """
            using Namotion.Interceptor.Attributes;

            namespace MetadataTests;

            [InterceptorSubject]
            public partial class MetadataRoot
            {
                public partial string? Name { get; set; }
            }
            """;

        const string childSource = """
            using Namotion.Interceptor.Attributes;

            namespace MetadataTests;

            [InterceptorSubject]
            public partial class Child : MetadataRoot
            {
                public partial string? ChildName { get; set; }
            }
            """;

        // Act
        var generatedChild = GenerateChildAgainstMetadataBase(baseSource, childSource);

        // Assert
        Assert.Contains(BareRaiseCallSite, generatedChild);
        Assert.DoesNotContain(WrappedRaiseCallSite, generatedChild);
    }

    [Fact]
    public void WhenMetadataBaseManuallyImplementsInpc_ThenChildWrapsRaiseCallSite()
    {
        // Arrange: a generated subject that declares and implements IRaisePropertyChanged manually;
        // its generation saw inherited INPC and emitted no wrapped raise, so the metadata type has
        // no GeneratedCode marker on RaisePropertyChanged.
        const string baseSource = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace MetadataTests;

            [InterceptorSubject]
            public partial class MetadataManualRoot : INotifyPropertyChanged, IRaisePropertyChanged
            {
                public partial string? Name { get; set; }

                public event PropertyChangedEventHandler? PropertyChanged;

                public void RaisePropertyChanged(string propertyName)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }
            }
            """;

        const string childSource = """
            using Namotion.Interceptor.Attributes;

            namespace MetadataTests;

            [InterceptorSubject]
            public partial class Child : MetadataManualRoot
            {
                public partial string? ChildName { get; set; }
            }
            """;

        // Act
        var generatedChild = GenerateChildAgainstMetadataBase(baseSource, childSource);

        // Assert: the manual raise is unwrapped, so the child provides the local-origin scope.
        Assert.Contains(WrappedRaiseCallSite, generatedChild);
    }

    /// <summary>
    /// Compiles the base source (with the generator applied) into an in-memory assembly, then runs
    /// the generator on the child source against that metadata reference and returns the generated
    /// child code.
    /// </summary>
    private static string GenerateChildAgainstMetadataBase(string baseSource, string childSource)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        // Unlike the text-only snapshot tests, this helper emits the base assembly, so every
        // assembly the generated code uses must be referenced explicitly (the AppDomain list only
        // contains assemblies something already loaded).
        var requiredAssemblies = new[]
        {
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute).Assembly,
            typeof(System.Collections.Frozen.FrozenDictionary).Assembly,
            typeof(System.ComponentModel.INotifyPropertyChanged).Assembly,
            typeof(IInterceptorSubject).Assembly,
        };

        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Concat(requiredAssemblies)
            .Select(a => a.Location)
            .Distinct()
            .Select(location => MetadataReference.CreateFromFile(location))
            .Cast<MetadataReference>()
            .ToList();

        var baseCompilation = CSharpCompilation.Create(
            assemblyName: "MetadataBaseLib",
            syntaxTrees: [CSharpSyntaxTree.ParseText(baseSource, parseOptions)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver baseDriver = CSharpGeneratorDriver
            .Create(new[] { new InterceptorSubjectGenerator().AsSourceGenerator() }, parseOptions: parseOptions);
        baseDriver.RunGeneratorsAndUpdateCompilation(baseCompilation, out var baseOutput, out _);

        using var peStream = new MemoryStream();
        var emitResult = baseOutput.Emit(peStream);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)));

        var baseReference = MetadataReference.CreateFromImage(peStream.ToArray());

        var childCompilation = CSharpCompilation.Create(
            assemblyName: "MetadataChildLib",
            syntaxTrees: [CSharpSyntaxTree.ParseText(childSource, parseOptions)],
            references: references.Append(baseReference),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver childDriver = CSharpGeneratorDriver
            .Create(new[] { new InterceptorSubjectGenerator().AsSourceGenerator() }, parseOptions: parseOptions);
        childDriver = childDriver.RunGeneratorsAndUpdateCompilation(childCompilation, out _, out _);

        return childDriver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Single()
            .SourceText
            .ToString();
    }
}
