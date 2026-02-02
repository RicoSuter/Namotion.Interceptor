using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectMetadataExtractorTests
{
    [Fact]
    public void ExtractsInterfaceDefaultProperty()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface ISensor
{
    double Value { get; set; }
    string Status => Value > 0 ? ""Active"" : ""Inactive"";
}

[InterceptorSubject]
public partial class Sensor : ISensor
{
    public partial double Value { get; set; }
}";

        var (metadata, _) = ExtractMetadata(source, "Sensor");

        // Should have Value from class and Status from interface
        Assert.Equal(2, metadata.Properties.Count);

        var valueProperty = metadata.Properties.Single(p => p.Name == "Value");
        Assert.False(valueProperty.IsFromInterface);
        Assert.True(valueProperty.IsPartial);

        var statusProperty = metadata.Properties.Single(p => p.Name == "Status");
        Assert.True(statusProperty.IsFromInterface);
        Assert.True(statusProperty.IsDerived);  // Default implementations are derived
        Assert.False(statusProperty.IsPartial);
    }

    [Fact]
    public void SkipsInterfacePropertyAlreadyDeclaredInClass()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface ISensor
{
    double Value { get; set; }
    string Status => Value > 0 ? ""Active"" : ""Inactive"";
}

[InterceptorSubject]
public partial class Sensor : ISensor
{
    public partial double Value { get; set; }
    public string Status => ""Custom"";  // Override the default implementation
}";

        var (metadata, _) = ExtractMetadata(source, "Sensor");

        // Should have Value and Status from class, not interface
        Assert.Equal(2, metadata.Properties.Count);

        var statusProperty = metadata.Properties.Single(p => p.Name == "Status");
        Assert.False(statusProperty.IsFromInterface);  // Should be from class, not interface
    }

    [Fact]
    public void HandlesMultipleInterfacesWithSameDefaultProperty()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface ISensor
{
    string Status => ""Sensor"";
}

public interface IDevice
{
    string Status => ""Device"";
}

[InterceptorSubject]
public partial class SensorDevice : ISensor, IDevice
{
    public partial double Value { get; set; }
}";

        var (metadata, _) = ExtractMetadata(source, "SensorDevice");

        // Should have Value from class and only one Status from interfaces (first one)
        Assert.Equal(2, metadata.Properties.Count);

        var statusProperty = metadata.Properties.Single(p => p.Name == "Status");
        Assert.True(statusProperty.IsFromInterface);
    }

    [Fact]
    public void SkipsAbstractInterfaceProperties()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface ISensor
{
    double Value { get; set; }  // Abstract - no default implementation
}

[InterceptorSubject]
public partial class Sensor : ISensor
{
    public partial double Value { get; set; }
}";

        var (metadata, _) = ExtractMetadata(source, "Sensor");

        // Should only have Value from class (abstract interface property shouldn't be extracted)
        Assert.Single(metadata.Properties);
        Assert.False(metadata.Properties[0].IsFromInterface);
    }

    private static (Namotion.Interceptor.Generator.Models.SubjectMetadata Metadata, string GeneratedSource) ExtractMetadata(string source, string className)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();
        var classDeclaration = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First(c => c.Identifier.ValueText == className);

        var typeSymbol = semanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;

        var metadata = SubjectMetadataExtractor.Extract(
            typeSymbol!,
            classDeclaration,
            semanticModel,
            CancellationToken.None);

        var generatedSource = SubjectCodeGenerator.Generate(metadata);

        return (metadata, generatedSource);
    }
}
