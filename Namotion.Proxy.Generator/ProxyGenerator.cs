using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Linq;
using System.Text;

namespace Namotion.Proxy.Generator;

[Generator]
public class ProxyGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classWithAttributeProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
                transform: (ctx, ct) =>
                {
                    var classDeclaration = (ClassDeclarationSyntax)ctx.Node;
                    var model = ctx.SemanticModel;
                    var classSymbol = model.GetDeclaredSymbol(classDeclaration, ct);
                    return new
                    {
                        ClassNode = (ClassDeclarationSyntax)ctx.Node,
                        Properties = classDeclaration.Members
                            .OfType<PropertyDeclarationSyntax>()
                            .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword)))
                            .Select(p => new
                            {
                                Property = p,
                                Type = model.GetTypeInfo(p.Type, ct)
                            })
                            .ToArray()
                    };
                })
            .Where(static m => m is not null)!;

        var compilationAndClasses = context.CompilationProvider.Combine(classWithAttributeProvider.Collect());

        context.RegisterSourceOutput(compilationAndClasses, (spc, source) =>
        {
            var (compilation, classes) = source;
            foreach (var cls in classes)
            {
                var baseClassName = cls.ClassNode.Identifier.ValueText;
                var newClassName = baseClassName.Replace("Base", string.Empty);
         
                var namespaceName = (cls.ClassNode.Parent as NamespaceDeclarationSyntax)?.Name.ToString() ?? "YourDefaultNamespace";

                var generatedCode = $@"
using Namotion.Proxy;

namespace {namespaceName} 
{{
    public class {newClassName} : {baseClassName}, IProxyContextProvider
    {{
        private IProxyContext _context;

        IProxyContext IProxyContextProvider.Context => _context;

        public {newClassName}(IProxyContextProvider proxyContextProvider)
        {{
            _context = proxyContextProvider.Context;
        }}
";
                foreach (var property in cls.Properties)
                {
                    var symbolDisplayFormat = new SymbolDisplayFormat(
typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

                    string fullyQualifiedName = property.Type.Type!.ToDisplayString(symbolDisplayFormat);

                    generatedCode +=
    $@"
        public override {fullyQualifiedName} {property.Property.Identifier.Value}
        {{
            get => GetProperty<{fullyQualifiedName}>(nameof({property.Property.Identifier.Value}), () => base.{property.Property.Identifier.Value});
            set => SetProperty(nameof({property.Property.Identifier.Value}), value, () => base.{property.Property.Identifier.Value}, v => base.{property.Property.Identifier.Value} = ({fullyQualifiedName})v!);
        }}
";
                }

                generatedCode +=
    $@"

        private T GetProperty<T>(string propertyName, Func<object?> readValue)
        {{
            return (T?)_context.GetProperty(this, propertyName, readValue)!;
        }}

        private void SetProperty<T>(string propertyName, T? newValue, Func<object?> readValue, Action<object?> setValue)
        {{
            _context.SetProperty(this, propertyName, newValue, readValue, setValue);
        }}
    }}
}}
";
                spc.AddSource($"{cls.ClassNode.Identifier.Value}.g.cs", SourceText.From(generatedCode, Encoding.UTF8));
            }
        });
    }
}
