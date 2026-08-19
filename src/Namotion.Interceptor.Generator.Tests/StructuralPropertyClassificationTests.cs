using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator.Tests;

public class StructuralPropertyClassificationTests
{
    [Fact]
    public void WhenGeneratedPropertyShapeIsKnown_ThenCompileTimeAndRuntimeClassificationAgree()
    {
        // Arrange
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(KnownShapesSource);

        // Act
        var properties = ExtractProperties(generated);
        var runtimeType = generated.LoadAssembly().GetType("ClassificationSubject")!;

        // Assert
        Assert.All(properties, property =>
        {
            var runtimePropertyType = runtimeType.GetProperty(property.Name)!.PropertyType;
            Assert.Equal(
                SubjectPropertyTypeClassifier.CanContainSubjects(runtimePropertyType),
                property.CanContainSubjects);
        });
    }

    [Fact]
    public void WhenGeneratedPropertyShapeIsAmbiguous_ThenClassificationIsConservative()
    {
        // Arrange
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(AmbiguousShapesSource);

        // Act
        var properties = ExtractProperties(generated);
        var runtimeType = generated.LoadAssembly().GetType("ClassificationSubject")!;

        // Assert
        Assert.All(properties, property =>
        {
            var runtimePropertyType = runtimeType.GetProperty(property.Name)!.PropertyType;
            Assert.Equal(
                SubjectPropertyTypeClassifier.CanContainSubjects(runtimePropertyType),
                property.CanContainSubjects);
        });
    }

    private static IReadOnlyList<PropertyMetadata> ExtractProperties(GeneratorRunResult generated)
    {
        var typeDeclaration = generated.OutputCompilation.SyntaxTrees
            .SelectMany(syntaxTree => syntaxTree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Single(type => type.Identifier.ValueText == "ClassificationSubject" && type.AttributeLists.Count > 0);
        var semanticModel = generated.OutputCompilation.GetSemanticModel(typeDeclaration.SyntaxTree);
        var typeSymbol = (INamedTypeSymbol)semanticModel.GetDeclaredSymbol(typeDeclaration)!;

        var declaredPropertyNames = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(property => property.Identifier.ValueText)
            .ToHashSet();

        return SubjectMetadataExtractor.Extract(typeSymbol, typeDeclaration, semanticModel, CancellationToken.None)
            .Metadata!
            .Properties
            .Where(property => declaredPropertyNames.Contains(property.Name))
            .ToList();
    }

    private const string KnownShapesSource = """
        using System;
        using System.Collections.Generic;
        using Namotion.Interceptor.Attributes;

        [InterceptorSubject]
        public partial class Subject
        {
        }

        [InterceptorSubject]
        public partial class ClassificationSubject
        {
            public int Primitive { get; set; }
            public string Text { get; set; } = string.Empty;
            public Subject Subject { get; set; } = new();
            public object Value { get; set; } = new();
            public IComparable Interface { get; set; } = 0;
            public IEnumerable<Subject> Enumerable { get; set; } = [];
            public IReadOnlyList<Subject> ReadOnlyList { get; set; } = [];
            public IDictionary<string, Subject> Dictionary { get; set; } = new Dictionary<string, Subject>();
            public IReadOnlyDictionary<string, Subject> ReadOnlyDictionary { get; set; } = new Dictionary<string, Subject>();
        }
        """;

    private const string AmbiguousShapesSource = """
        using System.Collections;
        using System.Collections.Generic;
        using Namotion.Interceptor.Attributes;

        [InterceptorSubject]
        public partial class Subject
        {
        }

        [InterceptorSubject]
        public partial class ClassificationSubject
        {
            public ArrayList NonGenericCollection { get; set; } = [];
            public Hashtable NonGenericDictionary { get; set; } = [];
            public IEnumerable<KeyValuePair<string, Subject>> EnumerablePairs { get; set; } = [];
            public List<IEnumerable<Subject>> NestedEnumerable { get; set; } = [];
        }
        """;
}
