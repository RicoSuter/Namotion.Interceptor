using System;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Namotion.Interceptor.Generator;

[Generator]
public class InterceptorSubjectGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classWithAttributeProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                // The attribute permits classes, so the compiler rejects structs and interfaces.
                // Record classes reach this predicate so extraction can report NI0003.
                predicate: (node, _) =>
                    node is ClassDeclarationSyntax { AttributeLists.Count: > 0 } or
                    RecordDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: (ctx, ct) =>
                {
                    var model = ctx.SemanticModel;
                    var typeDeclaration = (TypeDeclarationSyntax)ctx.Node;

                    var typeSymbol = model.GetDeclaredSymbol(typeDeclaration, ct);
                    if (typeSymbol is null)
                        return null;

                    var hasAttributeInAnyPartial = typeSymbol.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax(ct))
                        .OfType<TypeDeclarationSyntax>()
                        .Any(c =>
                        {
                            var declarationModel = model.Compilation.GetSemanticModel(c.SyntaxTree);
                            return HasInterceptorSubjectAttribute(c, declarationModel, ct);
                        });

                    return hasAttributeInAnyPartial
                        ? new
                        {
                            Model = model,
                            TypeDeclaration = typeDeclaration,
                            TypeSymbol = typeSymbol
                        }
                        : null;
                })
            .Select((tuple, _) =>
            {
                if (tuple is null)
                {
                    return null;
                }

                var typeSymbol = tuple.TypeSymbol;
                return new
                {
                    tuple.Model,
                    tuple.TypeDeclaration,
                    TypeSymbol = typeSymbol,
                    TypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                };
            })
            .Where(m => m is not null)
            .Collect()
            .SelectMany((items, _) => items
                .GroupBy(x => x!.TypeName)
                .Select(g => g.First()));

        // Cache availability as a bool so unrelated compilation changes do not invalidate this input.
        // Consumers without a System.Text.Json reference cannot compile the emitted attribute.
        var jsonIgnoreAvailableProvider = context.CompilationProvider.Select((compilation, _) =>
            compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute") is not null);

        context.RegisterSourceOutput(classWithAttributeProvider.Combine(jsonIgnoreAvailableProvider), (spc, pair) =>
        {
            var (cls, isJsonIgnoreAvailable) = pair;
            if (cls is null) return;

            EmitSource(spc, cls.Model, cls.TypeDeclaration, cls.TypeSymbol, cls.TypeName, isJsonIgnoreAvailable);
        });
    }

    private static void EmitSource(
        SourceProductionContext sourceProductionContext,
        SemanticModel semanticModel,
        TypeDeclarationSyntax typeDeclaration,
        INamedTypeSymbol typeSymbol,
        string fullyQualifiedTypeName,
        bool isJsonIgnoreAvailable)
    {
        try
        {
            var extraction = SubjectMetadataExtractor.Extract(
                typeSymbol,
                typeDeclaration,
                semanticModel,
                sourceProductionContext.CancellationToken);

            foreach (var diagnostic in extraction.Diagnostics)
            {
                sourceProductionContext.ReportDiagnostic(diagnostic);
            }

            if (extraction.Metadata is null)
            {
                return;
            }

            var fileName = SubjectCodeGenerator.GetFileName(extraction.Metadata);
            var generatedCode = SubjectCodeGenerator.Generate(extraction.Metadata, isJsonIgnoreAvailable);

            sourceProductionContext.AddSource(fileName, SourceText.From(generatedCode, Encoding.UTF8));
        }
        catch (Exception exception)
        {
            var className = typeDeclaration.Identifier.ValueText;

            sourceProductionContext.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.GeneratorFailed,
                typeDeclaration.Identifier.GetLocation(),
                className,
                exception.GetType().Name,
                exception.Message));

            // Preserve full exception details in a file. Qualify failure hint names so subjects sharing
            // a simple name do not collide and cause Roslyn to discard the entire generated output.
            sourceProductionContext.AddSource(GetFailureHintName(fullyQualifiedTypeName), SourceText.From($"/* {exception} */", Encoding.UTF8));
        }
    }

    /// <summary>
    /// Creates a failure hint name from a fully qualified type name, replacing unsupported characters.
    /// </summary>
    internal static string GetFailureHintName(string fullyQualifiedTypeName)
    {
        var builder = new StringBuilder(fullyQualifiedTypeName.Length + 5);
        foreach (var character in fullyQualifiedTypeName)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' ? character : '_');
        }

        return builder.Append(".g.cs").ToString();
    }

    private static bool HasInterceptorSubjectAttribute(TypeDeclarationSyntax typeDeclaration, SemanticModel semanticModel, CancellationToken ct)
    {
        return SymbolExtensions.HasAttribute(typeDeclaration.AttributeLists, KnownTypes.InterceptorSubjectAttribute, semanticModel, ct);
    }
}
