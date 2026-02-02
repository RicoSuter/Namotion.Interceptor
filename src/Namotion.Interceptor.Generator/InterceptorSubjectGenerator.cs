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
                predicate: (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: (ctx, ct) =>
                {
                    var model = ctx.SemanticModel;
                    var classDeclaration = (ClassDeclarationSyntax)ctx.Node;

                    // Get the type symbol to access all partial declarations
                    var typeSymbol = model.GetDeclaredSymbol(classDeclaration, ct);
                    if (typeSymbol is null)
                        return null;

                    // Check if ANY partial declaration has the InterceptorSubjectAttribute
                    var hasAttributeInAnyPartial = typeSymbol.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax(ct))
                        .OfType<ClassDeclarationSyntax>()
                        .Any(c =>
                        {
                            var declarationModel = model.Compilation.GetSemanticModel(c.SyntaxTree);
                            return HasInterceptorSubjectAttribute(c, declarationModel, ct);
                        });

                    return hasAttributeInAnyPartial
                        ? new
                        {
                            Model = model,
                            ClassNode = classDeclaration,
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
                    tuple.ClassNode,
                    TypeSymbol = typeSymbol,
                    TypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                };
            })
            .Where(m => m is not null)
            .Collect()
            .SelectMany((items, _) => items
                .GroupBy(x => x!.TypeName)
                .Select(g => g.First())); // take only one per type name to avoid duplicates

        context.RegisterSourceOutput(classWithAttributeProvider, (spc, cls) =>
        {
            if (cls is null) return;

            string fileName;
            try
            {
                var metadata = SubjectMetadataExtractor.Extract(
                    cls.TypeSymbol,
                    cls.ClassNode,
                    cls.Model,
                    CancellationToken.None);

                fileName = SubjectCodeGenerator.GetFileName(metadata);
                var generatedCode = SubjectCodeGenerator.Generate(metadata);

                spc.AddSource(fileName, SourceText.From(generatedCode, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                // Fallback filename using available info
                var className = cls.ClassNode.Identifier.ValueText;
                fileName = $"{className}.g.cs";
                spc.AddSource(fileName, SourceText.From($"/* {ex} */", Encoding.UTF8));
            }
        });
    }

    private bool HasInterceptorSubjectAttribute(ClassDeclarationSyntax classDeclaration, SemanticModel semanticModel, CancellationToken ct)
    {
        return HasAttribute(classDeclaration.AttributeLists, "Namotion.Interceptor.Attributes.InterceptorSubjectAttribute", semanticModel, ct);
    }

    private bool HasAttribute(SyntaxList<AttributeListSyntax> attributeLists, string baseTypeName, SemanticModel semanticModel, CancellationToken ct)
    {
        var hasAttribute = attributeLists
            .SelectMany(al => al.Attributes)
            .Any(attr =>
            {
                var attributeType = semanticModel.GetTypeInfo(attr, ct).Type as INamedTypeSymbol;
                return attributeType is not null && IsTypeOrInheritsFrom(attributeType, baseTypeName);
            });

        return hasAttribute;
    }

    private bool IsTypeOrInheritsFrom(ITypeSymbol? type, string fullTypeName)
    {
        do
        {
            if (type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == fullTypeName)
            {
                return true;
            }

            type = type?.BaseType;
        } while (type is not null);

        return false;
    }
}
