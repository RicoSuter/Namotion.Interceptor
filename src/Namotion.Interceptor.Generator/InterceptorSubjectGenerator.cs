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
    private const string InterceptedMethodPostfix = "WithoutInterceptor";
    
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classWithAttributeProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: (ctx, ct) =>
                {
                    var classDeclaration = (ClassDeclarationSyntax)ctx.Node;

                    var model = ctx.SemanticModel;
                    if (!HasInterceptorSubjectAttribute(classDeclaration, model, ct))
                        return null!;
                    
                    return new
                    {
                        Model = model,
                        ClassNode = (ClassDeclarationSyntax)ctx.Node,
                        Properties = classDeclaration.Members
                            .OfType<PropertyDeclarationSyntax>()
                            .Select(p => new
                            {
                                Property = p,
                                Type = model.GetTypeInfo(p.Type, ct),
                                AccessModifier = 
                                    p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) ? "public" :
                                    p.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)) ? "internal" :
                                    p.Modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword)) ? "protected" : 
                                    "private",

                                IsPartial = p.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
                                IsDerived = HasDerivedAttribute(p, model, ct),
                                IsRequired = p.Modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword)),
                                HasGetter = p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true ||
                                            p.ExpressionBody.IsKind(SyntaxKind.ArrowExpressionClause),
                                HasSetter = p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true,
                                HasInit = p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true
                            })
                            .ToArray(),
                        Methods = classDeclaration.Members
                            .OfType<MethodDeclarationSyntax>()
                            .Where(p => p.Identifier.Text.EndsWith(InterceptedMethodPostfix))
                            .Select(p => new
                            {
                                Method = p,
                                ReturnType = p.ReturnType,
                                Parameters = p.ParameterList.Parameters
                            })
                            .ToArray()
                    };
                })
            .Where(static m => m is not null)!;

        var compilationAndClasses = context.CompilationProvider.Combine(classWithAttributeProvider.Collect());

        context.RegisterSourceOutput(compilationAndClasses, (spc, source) =>
        {
            var (_, classes) = source;
            foreach (var cls in classes.GroupBy(c => c.ClassNode.Identifier.ValueText))
            {
                var fileName = $"{cls.First().ClassNode.Identifier.Value}.g.cs";
                try
                {
                    var semanticModel = cls.First().Model;
                    var className = cls.First().ClassNode.Identifier.ValueText;
                    
                    var baseClass = cls.First().ClassNode.BaseList?.Types
                        .Select(t => semanticModel.GetTypeInfo(t.Type).Type as INamedTypeSymbol)
                        .FirstOrDefault(t => t != null && 
                            (HasInterceptorSubjectAttribute(t) || // <= needed when partial class with IInterceptorSubject is not yet generated
                             ImplementsInterface(t, "Namotion.Interceptor.IInterceptorSubject")));
                    
                    var baseClassTypeName = baseClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    
                    var namespaceName = (cls.First().ClassNode.Parent as NamespaceDeclarationSyntax)?.Name.ToString() ??
                                        (cls.First().ClassNode.Parent as FileScopedNamespaceDeclarationSyntax)?.Name.ToString()
                                        ?? "YourDefaultNamespace";

                    var defaultPropertiesNewModifier = baseClass is not null ? "new " : string.Empty;

                    var generatedCode = $@"// <auto-generated>
//     This code was generated by Namotion.Interceptor.Generator
// </auto-generated>

using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

#pragma warning disable CS8669
#pragma warning disable CS0649

namespace {namespaceName} 
{{
    public partial class {className} : IInterceptorSubject
    {{
        private IInterceptorSubjectContext? _context;
        private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

        [JsonIgnore]
        IInterceptorSubjectContext IInterceptorSubject.Context => _context ??= new InterceptorExecutor(this);

        [JsonIgnore]
        ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data {{ get; }} = new();

        [JsonIgnore]
        IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties ?? DefaultProperties;

        [JsonIgnore]
        object IInterceptorSubject.SyncRoot {{ get; }} = new object();

        void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
        {{
            _properties = (_properties ?? DefaultProperties)
                .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                .ToFrozenDictionary();
        }}

        public {defaultPropertiesNewModifier}static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties {{ get; }} =
            new Dictionary<string, SubjectPropertyMetadata>
            {{";
                    foreach (var property in cls.SelectMany(c => c.Properties))
                    {
                        var fullyQualifiedName = property.Type.Type!.ToString();
                        var propertyName = property.Property.Identifier.Value;

                        generatedCode +=
    $@"
                {{
                    ""{propertyName}"",       
                    new SubjectPropertyMetadata(
                        typeof({className}).GetProperty(nameof({propertyName}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!, 
                        {(property.HasGetter ? ($"(o) => (({className})o).{propertyName}") : "null")}, 
                        {(property.HasSetter ? ($"(o, v) => (({className})o).{propertyName} = ({fullyQualifiedName})v") : "null")}, 
                        isIntercepted: {(property.IsPartial ? "true" : "false")},
                        isDynamic: false)
                }},";
                    }

                    generatedCode +=
    $@"
            }}
            {( baseClassTypeName is not null ? $".Concat({baseClassTypeName}.DefaultProperties)" : string.Empty )}
            .ToFrozenDictionary();
";

                    var firstConstructor = cls.SelectMany(c => c.ClassNode.Members)
                        .FirstOrDefault(m => m.IsKind(SyntaxKind.ConstructorDeclaration))
                        as ConstructorDeclarationSyntax;

                    if (firstConstructor == null)
                    {
                        generatedCode +=
    $@"
        public {className}()
        {{
        }}
";
                    }

                    if (firstConstructor == null ||
                        firstConstructor.ParameterList.Parameters.Count == 0)
                    {
                        generatedCode +=
    $@"
        public {className}(IInterceptorSubjectContext context) : this()
        {{
            ((IInterceptorSubject)this).Context.AddFallbackContext(context);
        }}
";
                    }

                    foreach (var property in cls.SelectMany(c => c.Properties).Where(p => p.IsPartial))
                    {
                        var fullyQualifiedName = property.Type.Type!.ToString();
                        var propertyName = property.Property.Identifier.Value;
                        var propertyModifier = property.AccessModifier;

                        generatedCode +=
    $@"
        private {fullyQualifiedName} _{propertyName};

        {propertyModifier} {(property.IsRequired ? "required " : "")}partial {fullyQualifiedName} {propertyName}
        {{";
                        if (property.HasGetter)
                        {
                            var modifiers = string.Join(" ", property.Property.AccessorList?
                                .Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)).Modifiers.Select(m => m.Value) ?? []);

                            generatedCode +=
    $@"
            {modifiers} get => GetPropertyValue<{fullyQualifiedName}>(nameof({propertyName}), static (o) => (({className})o)._{propertyName});";

                        }

                        if (property.HasSetter || property.HasInit)
                        {
                            var accessor = property.Property.AccessorList?
                                .Accessors.Single(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) 
                                ?? throw new InvalidOperationException("Accessor not found.");

                            var accessorText = accessor.IsKind(SyntaxKind.InitAccessorDeclaration) ? "init" : "set";
                            var modifiers = string.Join(" ", accessor.Modifiers.Select(m => m.Value) ?? []);

                            generatedCode +=
    $@"
            {modifiers} {accessorText} => SetPropertyValue(nameof({propertyName}), value, static (o) => (({className})o)._{propertyName}, static (o, v) => (({className})o)._{propertyName} = v);";
                        }

                        generatedCode +=
    $@"
        }}
";
                    }

                    foreach (var method in cls.SelectMany(c => c.Methods))
                    {
                        var fullMethodName = method.Method.Identifier.Text;
                        var methodName = fullMethodName.Substring(0, fullMethodName.Length - InterceptedMethodPostfix.Length);
                        var returnType = GetFullTypeName(method.ReturnType, semanticModel);
                        var parameters = method.Parameters.Select(p => new
                        {
                            Name = p.Identifier.ValueText,
                            Type = GetFullTypeName(p.Type, semanticModel)
                        }).ToList();

                        var directParameterCode = string.Join(", ", parameters.Select((p, i) => $"({p.Type})p[{i}]!"));
                        var invokeParameterCode = parameters.Any() ? ", " + string.Join(", ", parameters.Select(p => p.Name)) : string.Empty;

                        if (returnType != "void")
                        {
                            generatedCode += 
    $@"
        public {returnType} {methodName}({string.Join(", ", parameters.Select(p => p.Type + " " + p.Name))})
        {{
            return ({returnType})InvokeMethod(""{methodName}"", static (s, p) => (({className})s).{fullMethodName}({directParameterCode}){invokeParameterCode})!;
        }}
";
                        }
                        else
                        {
                            generatedCode += 
    $@"
        public {returnType} {methodName}({string.Join(", ", parameters.Select(p => p.Type + " " + p.Name))})
        {{
            InvokeMethod(""{methodName}"", static (s, p) => {{ (({className})s).{fullMethodName}({directParameterCode}); return null; }}{invokeParameterCode});
        }}
";
                        }
                    }

                    generatedCode +=
    $@"
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
        {{
            if (_context is not null)
            {{
                var readContext = new PropertyReadContext(this, propertyName);
                return _context.ExecuteInterceptedRead(ref readContext, readValue);
            }}

            return readValue(this);
        }}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<IInterceptorSubject, TProperty> readValue, Action<IInterceptorSubject, TProperty> setValue)
        {{
            if (_context is not null)
            {{
                var writeContext = new PropertyWriteContext<TProperty>(this, propertyName, readValue, newValue);
                _context.ExecuteInterceptedWrite(ref writeContext, setValue);
            }}
            else
            {{
                setValue(this, newValue);
            }}
        }}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
        {{
            if (_context is not null)
            {{
                var invocationContext = new MethodInvocationContext<TProperty>(this, methodName, parameters);
                _context.ExecuteInterceptedInvoke(ref invocationContext, invokeMethod);
            }}
            else
            {{
                return invokeMethod(this, parameters);
            }}
        }}
    }}
}}
";
                    spc.AddSource(fileName, SourceText.From(generatedCode, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    spc.AddSource(fileName, SourceText.From($"/* {ex} */", Encoding.UTF8));
                }
            }
        });
    }

    private bool HasDerivedAttribute(PropertyDeclarationSyntax property, SemanticModel semanticModel, CancellationToken ct)
    {
        return HasAttribute(property.AttributeLists, "Namotion.Interceptor.Attributes.DerivedAttribute", semanticModel, ct);
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
    
    private static bool HasInterceptorSubjectAttribute(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        return type
            .GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == 
                "Namotion.Interceptor.Attributes.InterceptorSubjectAttribute");
    }
    
    private bool ImplementsInterface(ITypeSymbol? type, string interfaceTypeName)
    {
        if (type is null)
        {
            return false;
        }

        if (type.TypeKind == TypeKind.Interface && 
            type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName)
        {
            return true;
        }

        if (type
            .AllInterfaces
            .Any(i => i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName))
        {
            return true;
        }

        return type.BaseType is { } baseType && 
            ImplementsInterface(baseType, interfaceTypeName);
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

    private string? GetFullTypeName(TypeSyntax? type, SemanticModel semanticModel)
    {
        if (type == null)
            return null;

        var typeInfo = semanticModel.GetTypeInfo(type);
        var symbol = typeInfo.Type;
        if (symbol != null)
        {
            return GetFullTypeName(symbol);
        }

        throw new InvalidOperationException("Unable to resolve type symbol.");
    }

    static string? GetFullTypeName(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } namedTypeSymbol)
        {
            var genericArguments = string.Join(", ", namedTypeSymbol.TypeArguments.Select(GetFullTypeName));
            return $"{namedTypeSymbol.ContainingNamespace}.{namedTypeSymbol.Name}<{genericArguments}>";
        }

        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
