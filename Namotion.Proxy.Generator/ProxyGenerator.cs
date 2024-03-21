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
                predicate: (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0 && cds.Identifier.Value?.ToString().EndsWith("Base") == true,
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
                                Type = model.GetTypeInfo(p.Type, ct),
                                IsRequired = p.Modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword)),
                                IsDerived = p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) != true
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
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

#pragma warning disable CS8669

namespace {namespaceName} 
{{
    public class {newClassName} : {baseClassName}, IProxy
    {{
        private IProxyContext? _context;
        private ConcurrentDictionary<string, object?> _data = new ConcurrentDictionary<string, object?>();

        IProxyContext? IProxy.Context
        {{
            get => _context;
            set => _context = value;
        }}

        ConcurrentDictionary<string, object?> IProxy.Data => _data;
        IReadOnlyDictionary<string, PropertyInfo> IProxy.Properties => _properties;

        private static IReadOnlyDictionary<string, PropertyInfo> _properties = new Dictionary<string, PropertyInfo>
        {{
";
                foreach (var property in cls.Properties)
                {
                    var symbolDisplayFormat = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);
                    var fullyQualifiedName = property.Type.Type!.ToDisplayString(symbolDisplayFormat);
                    var propertyName = property.Property.Identifier.Value;

                    generatedCode +=
$@"
            {{
                ""{propertyName}"",       
                new PropertyInfo(nameof({propertyName}), typeof({baseClassName}).GetProperty(nameof({propertyName}))!, {(property.IsDerived ? "true" : "false")}, (o) => (({newClassName})o).{propertyName})
            }},
";
                }

                    generatedCode +=
$@"
        }};

        public {newClassName}(IProxyContext? context = null)
        {{
            if (context is not null)
            {{
                this.SetContext(context);
            }}
        }}
";
                foreach (var property in cls.Properties)
                {
                    var symbolDisplayFormat = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);
                    var fullyQualifiedName = property.Type.Type!.ToDisplayString(symbolDisplayFormat);
                    var propertyName = property.Property.Identifier.Value;

                    generatedCode +=
$@"
        public override {(property.IsRequired ? "required" : "")} {fullyQualifiedName} {propertyName}
        {{
";
                    if (property.Property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true ||
                        property.Property.ExpressionBody.IsKind(SyntaxKind.ArrowExpressionClause))
                    {
                        generatedCode +=
$@"
            get => GetProperty<{fullyQualifiedName}>(nameof({propertyName}), () => base.{propertyName});
";

                    }

                    if (property.IsDerived == false)
                    {
                        generatedCode +=
$@"
            set => SetProperty(nameof({propertyName}), value, () => base.{propertyName}, v => base.{propertyName} = ({fullyQualifiedName})v!);
"; 
                    }

                    generatedCode +=
$@"
        }}
";
                }

                generatedCode +=
$@"

        private T GetProperty<T>(string propertyName, Func<object?> readValue)
        {{
            return _context is not null ? (T?)_context.GetProperty(this, propertyName, readValue)! : (T?)readValue()!;
        }}

        private void SetProperty<T>(string propertyName, T? newValue, Func<object?> readValue, Action<object?> setValue)
        {{
            if (_context is null)
            {{
                setValue(newValue);
            }}
            else
            {{
                _context.SetProperty(this, propertyName, newValue, readValue, setValue);
            }}
        }}
    }}
}}
";
                spc.AddSource($"{cls.ClassNode.Identifier.Value}.g.cs", SourceText.From(generatedCode, Encoding.UTF8));
            }
        });
    }
}
