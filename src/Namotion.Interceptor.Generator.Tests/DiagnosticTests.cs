using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

public class DiagnosticTests
{
    [Fact]
    public void WhenTwoMethodsWithSameNameAreMarkedSubjectMethod_ThenNI0001IsReported()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Sample;

            [InterceptorSubject]
            public partial class Compressor
            {
                [SubjectMethod]
                public void Start() { }

                [SubjectMethod]
                public void Start(int delayMs) { }
            }
            """;

        // Act
        var diagnostics = GetGeneratorDiagnostics(source);

        // Assert
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == "NI0001" &&
            diagnostic.GetMessage().Contains("Start"));
    }

    [Fact]
    public void WhenClassImplementsInterfaceSubjectMethod_ThenNoDiagnosticIsReported()
    {
        // Arrange
        const string source = """
            using System.Threading.Tasks;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Sample;

            public interface IStartable
            {
                [SubjectMethod]
                Task StartAsync();
            }

            [InterceptorSubject]
            public partial class Compressor : IStartable
            {
                [SubjectMethod]
                public Task StartAsync() => Task.CompletedTask;
            }
            """;

        // Act
        var diagnostics = GetGeneratorDiagnostics(source);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void WhenTwoInterfacesDeclareSameSubjectMethodName_ThenNI0001IsReported()
    {
        // Arrange
        const string source = """
            using System.Threading.Tasks;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Sample;

            public interface IStartable
            {
                [SubjectMethod]
                Task StartAsync();
            }

            public interface IAlsoStartable
            {
                [SubjectMethod]
                Task StartAsync();
            }

            [InterceptorSubject]
            public partial class Compressor : IStartable, IAlsoStartable
            {
            }
            """;

        // Act
        var diagnostics = GetGeneratorDiagnostics(source);

        // Assert
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == "NI0001" &&
            diagnostic.GetMessage().Contains("StartAsync"));
    }

    [Fact]
    public void WhenStaticMethodIsMarkedSubjectMethod_ThenNI0002IsReported()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Sample;

            [InterceptorSubject]
            public partial class Utility
            {
                [SubjectMethod]
                public static void Ping() { }
            }
            """;

        // Act
        var diagnostics = GetGeneratorDiagnostics(source);

        // Assert
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == "NI0002" &&
            diagnostic.GetMessage().Contains("Ping"));
    }

    [Fact]
    public void WhenStaticInterfaceMethodIsMarkedSubjectMethod_ThenNI0002IsReported()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Sample;

            public interface IUtility
            {
                [SubjectMethod]
                static void Ping() { }
            }

            [InterceptorSubject]
            public partial class Utility : IUtility
            {
            }
            """;

        // Act
        var diagnostics = GetGeneratorDiagnostics(source);

        // Assert
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == "NI0002" &&
            diagnostic.GetMessage().Contains("Ping"));
    }

    private static IReadOnlyList<Diagnostic> GetGeneratorDiagnostics(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "DiagGen",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new InterceptorSubjectGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }
}
