using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Pins the INPC raise rule for base types that are only available as metadata (compiled in
/// another assembly): generated and manual protected raisers are called directly, while an
/// explicit-only manual implementation receives a protected forwarder from the generator.
/// </summary>
public class MetadataBaseRaiseTests
{
    [Fact]
    public void WhenMetadataBaseIsGeneratedSubject_ThenChildCallsInheritedRaise()
    {
        // Arrange: an ordinary generated subject compiled to metadata; the child calls the
        // inherited protected raise directly.
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
        var generatedChild = GenerateAndCompileChildAgainstMetadataBase(baseSource, childSource);

        // Assert
        Assert.Contains(
            "                    RaisePropertyChanged(nameof(ChildName));",
            generatedChild);
        Assert.DoesNotContain(
            "        protected void RaisePropertyChanged(string propertyName) =>",
            generatedChild);
    }

    [Fact]
    public void WhenMetadataBaseManuallyImplementsProtectedRaise_ThenChildCallsItDirectly()
    {
        // Arrange: a hand-written INPC base (no [InterceptorSubject]) compiled to metadata; its
        // protected raise owns the local-origin contract and is directly callable by the generated
        // child; the interface implementation remains explicit.
        const string baseSource = """
            using System.ComponentModel;
            using Namotion.Interceptor;

            namespace MetadataTests;

            public abstract class ManualMetadataBase : INotifyPropertyChanged, IRaisePropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;

                protected void RaisePropertyChanged(string propertyName)
                {
                    var handler = PropertyChanged;
                    if (handler is null)
                    {
                        return;
                    }

                    using (SubjectChangeContext.WithLocalOrigin())
                    {
                        handler(this, new PropertyChangedEventArgs(propertyName));
                    }
                }

                void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) =>
                    RaisePropertyChanged(propertyName);
            }
            """;

        const string childSource = """
            using Namotion.Interceptor.Attributes;

            namespace MetadataTests;

            [InterceptorSubject]
            public partial class Child : ManualMetadataBase
            {
                public partial string? ChildName { get; set; }
            }
            """;

        // Act
        var generatedChild = GenerateAndCompileChildAgainstMetadataBase(baseSource, childSource);

        // Assert
        Assert.Contains(
            "                    RaisePropertyChanged(nameof(ChildName));",
            generatedChild);
        Assert.DoesNotContain(
            "        protected void RaisePropertyChanged(string propertyName) =>",
            generatedChild);
    }

    [Fact]
    public void WhenGeneratedMetadataBaseImplementsRaiseExplicitly_ThenChildUsesInheritedHelperAndCompiles()
    {
        // Arrange: the generated base owns INPC manually and exposes only an explicit interface
        // method. The generator emits a protected forwarding method used by descendants.
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

                void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
                {
                    var handler = PropertyChanged;
                    if (handler is null)
                    {
                        return;
                    }

                    using (SubjectChangeContext.WithLocalOrigin())
                    {
                        handler(this, new PropertyChangedEventArgs(propertyName));
                    }
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
        var generatedChild = GenerateAndCompileChildAgainstMetadataBase(baseSource, childSource);

        // Assert
        Assert.Contains(
            "                    RaisePropertyChanged(nameof(ChildName));",
            generatedChild);
        Assert.DoesNotContain(
            "        protected void RaisePropertyChanged(string propertyName) =>",
            generatedChild);
    }

    /// <summary>
    /// Compiles the base source (with the generator applied) into an in-memory assembly, runs the
    /// generator on the child source against that metadata reference, asserts the child output
    /// compiles, and returns the generated child code.
    /// </summary>
    private static string GenerateAndCompileChildAgainstMetadataBase(string baseSource, string childSource)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        // Unlike the text-only snapshot tests, this helper emits both assemblies, so every
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

        var baseReference = MetadataReference.CreateFromImage(EmitSucceeding(baseOutput));

        var childCompilation = CSharpCompilation.Create(
            assemblyName: "MetadataChildLib",
            syntaxTrees: [CSharpSyntaxTree.ParseText(childSource, parseOptions)],
            references: references.Append(baseReference),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver childDriver = CSharpGeneratorDriver
            .Create(new[] { new InterceptorSubjectGenerator().AsSourceGenerator() }, parseOptions: parseOptions);
        childDriver = childDriver.RunGeneratorsAndUpdateCompilation(childCompilation, out var childOutput, out _);

        // The generated child must actually compile against the metadata base, not just contain
        // the expected text (a wrong raise call shape would be green otherwise).
        EmitSucceeding(childOutput);

        return childDriver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Single()
            .SourceText
            .ToString();
    }

    private static byte[] EmitSucceeding(Compilation compilation)
    {
        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)));
        return peStream.ToArray();
    }
}
