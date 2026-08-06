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
                // A struct or interface can never be a valid subject (InterceptorSubjectAttribute's
                // AttributeUsage is Class-only, so the compiler already reports CS0592 on those),
                // so excluding them here keeps GetDeclaredSymbol and GetSemanticModel below from
                // running per attributed struct and interface on every keystroke in an IDE.
                // Records are deliberately kept: the compiler accepts the attribute on a record
                // class, so NI0003 below is the only report that case gets.
                predicate: (node, _) =>
                    node is ClassDeclarationSyntax { AttributeLists.Count: > 0 } or
                    RecordDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: (ctx, ct) =>
                {
                    var model = ctx.SemanticModel;
                    var typeDeclaration = (TypeDeclarationSyntax)ctx.Node;

                    // Get the type symbol to access all partial declarations
                    var typeSymbol = model.GetDeclaredSymbol(typeDeclaration, ct);
                    if (typeSymbol is null)
                        return null;

                    // Check if ANY partial declaration has the InterceptorSubjectAttribute
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
                .Select(g => g.First())); // take only one per type name to avoid duplicates

        context.RegisterSourceOutput(classWithAttributeProvider, (spc, cls) =>
        {
            if (cls is null) return;

            try
            {
                var extraction = SubjectMetadataExtractor.Extract(
                    cls.TypeSymbol,
                    cls.TypeDeclaration,
                    cls.Model,
                    spc.CancellationToken);

                foreach (var diagnostic in extraction.Diagnostics)
                {
                    spc.ReportDiagnostic(diagnostic);
                }

                if (extraction.Metadata is null)
                {
                    return;
                }

                var fileName = SubjectCodeGenerator.GetFileName(extraction.Metadata);
                var generatedCode = SubjectCodeGenerator.Generate(extraction.Metadata);

                spc.AddSource(fileName, SourceText.From(generatedCode, Encoding.UTF8));
            }
            catch (Exception exception)
            {
                var className = cls.TypeDeclaration.Identifier.ValueText;

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.GeneratorFailed,
                    cls.TypeDeclaration.Identifier.GetLocation(),
                    className,
                    exception.GetType().Name,
                    exception.Message));

                // The file keeps the full frames; a diagnostic message renders as one line.
                spc.AddSource($"{className}.g.cs", SourceText.From($"/* {exception} */", Encoding.UTF8));
            }
        });
    }

    private static bool HasInterceptorSubjectAttribute(TypeDeclarationSyntax typeDeclaration, SemanticModel semanticModel, CancellationToken ct)
    {
        return SymbolExtensions.HasAttribute(typeDeclaration.AttributeLists, KnownTypes.InterceptorSubjectAttribute, semanticModel, ct);
    }
}
