using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectMetadataExtractor
{
    private const string InterceptedMethodPostfix = "WithoutInterceptor";

    /// <summary>
    /// Extracts metadata from a class declaration with the InterceptorSubject attribute.
    /// </summary>
    public static SubjectMetadata Extract(
        INamedTypeSymbol typeSymbol,
        ClassDeclarationSyntax classDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var className = classDeclaration.Identifier.ValueText;
        var containingTypes = GetContainingTypes(classDeclaration);
        var namespaceName = GetNamespace(classDeclaration);
        var fullTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Detect base class
        var baseClass = classDeclaration.BaseList?.Types
            .Select(t => semanticModel.GetTypeInfo(t.Type, cancellationToken).Type as INamedTypeSymbol)
            .FirstOrDefault(t => t != null &&
                (HasInterceptorSubjectAttribute(t) ||
                 ImplementsInterface(t, "Namotion.Interceptor.IInterceptorSubject")));

        var baseClassTypeName = baseClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Check if base class has INotifyPropertyChanged
        var baseClassHasInpc = HasInterceptorSubjectAttribute(baseClass) ||
            (classDeclaration.BaseList?.Types
                .Select(t => semanticModel.GetTypeInfo(t.Type, cancellationToken).Type as INamedTypeSymbol)
                .Any(t => t != null && ImplementsInterface(t, "Namotion.Interceptor.IRaisePropertyChanged")) ?? false);

        // Collect all partial class declarations
        var allClassDeclarations = typeSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .ToArray();

        // Collect properties from all partial declarations
        var classProperties = CollectProperties(typeSymbol, semanticModel, cancellationToken);

        // Collect interface properties with default implementations
        var interfaceProperties = ExtractInterfaceDefaultProperties(typeSymbol, classProperties);

        // Combine class properties with interface default properties
        var properties = classProperties.Concat(interfaceProperties).ToList();

        // Collect methods from all partial declarations
        var methods = CollectMethods(typeSymbol, semanticModel, cancellationToken);

        // Detect constructor state
        var (needsGeneratedParameterlessConstructor, hasOrWillHaveParameterlessConstructor) =
            DetectConstructorState(allClassDeclarations);

        return new SubjectMetadata(
            className,
            namespaceName,
            fullTypeName,
            containingTypes,
            needsGeneratedParameterlessConstructor,
            hasOrWillHaveParameterlessConstructor,
            baseClassTypeName,
            baseClassHasInpc,
            properties,
            methods);
    }

    private static string GetNamespace(ClassDeclarationSyntax classDeclaration)
    {
        // Walk up past containing types to find namespace
        SyntaxNode? current = classDeclaration.Parent;
        while (current is TypeDeclarationSyntax)
        {
            current = current.Parent;
        }

        return (current as NamespaceDeclarationSyntax)?.Name.ToString() ??
               (current as FileScopedNamespaceDeclarationSyntax)?.Name.ToString() ??
               "YourDefaultNamespace";
    }

    private static string[] GetContainingTypes(SyntaxNode node)
    {
        var types = new List<string>();
        var parent = node.Parent;
        while (parent is TypeDeclarationSyntax typeDecl)
        {
            types.Insert(0, typeDecl.Identifier.ValueText);
            parent = parent.Parent;
        }
        return types.ToArray();
    }

    private static IReadOnlyList<PropertyMetadata> CollectProperties(
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var properties = new List<PropertyMetadata>();

        foreach (var syntaxReference in typeSymbol.DeclaringSyntaxReferences)
        {
            var declaration = syntaxReference.GetSyntax(cancellationToken);
            if (declaration is not ClassDeclarationSyntax classDecl)
            {
                continue;
            }

            var declarationModel = semanticModel.Compilation.GetSemanticModel(classDecl.SyntaxTree);

            foreach (var property in classDecl.Members.OfType<PropertyDeclarationSyntax>())
            {
                var typeInfo = declarationModel.GetTypeInfo(property.Type, cancellationToken);
                var fullyQualifiedName = typeInfo.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "object";
                var propertyName = property.Identifier.ValueText;

                var accessModifier = GetAccessModifier(property.Modifiers);
                var isPartial = property.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
                var isVirtual = property.Modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword));
                var isOverride = property.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));
                var isDerived = HasDerivedAttribute(property, declarationModel, cancellationToken);
                var isRequired = property.Modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword));

                var hasGetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true ||
                                property.ExpressionBody != null;
                var hasSetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;
                var hasInit = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

                var getterAccessModifier = GetAccessorModifier(property.AccessorList, SyntaxKind.GetAccessorDeclaration);
                var setterAccessModifier = GetAccessorModifier(property.AccessorList, SyntaxKind.SetAccessorDeclaration) ??
                                           GetAccessorModifier(property.AccessorList, SyntaxKind.InitAccessorDeclaration);

                properties.Add(new PropertyMetadata(
                    propertyName,
                    fullyQualifiedName,
                    accessModifier,
                    isPartial,
                    isVirtual,
                    isOverride,
                    isDerived,
                    isRequired,
                    hasGetter,
                    hasSetter,
                    hasInit,
                    IsFromInterface: false,
                    getterAccessModifier,
                    setterAccessModifier));
            }
        }

        return properties;
    }

    private static IReadOnlyList<MethodMetadata> CollectMethods(
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var methods = new List<MethodMetadata>();

        foreach (var syntaxReference in typeSymbol.DeclaringSyntaxReferences)
        {
            var declaration = syntaxReference.GetSyntax(cancellationToken);
            if (declaration is not ClassDeclarationSyntax classDecl)
            {
                continue;
            }

            var declarationModel = semanticModel.Compilation.GetSemanticModel(classDecl.SyntaxTree);

            foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var fullMethodName = method.Identifier.Text;
                if (!fullMethodName.EndsWith(InterceptedMethodPostfix))
                {
                    continue;
                }

                var methodName = fullMethodName.Substring(0, fullMethodName.Length - InterceptedMethodPostfix.Length);
                var returnType = GetFullTypeName(method.ReturnType, declarationModel);

                var parameters = method.ParameterList.Parameters
                    .Select(p => new ParameterMetadata(
                        p.Identifier.ValueText,
                        GetFullTypeName(p.Type, declarationModel) ?? "object"))
                    .ToList();

                methods.Add(new MethodMetadata(
                    methodName,
                    fullMethodName,
                    returnType ?? "void",
                    parameters));
            }
        }

        return methods;
    }

    /// <summary>
    /// Extracts properties with default implementations from all interfaces implemented by the type.
    /// </summary>
    private static IReadOnlyList<PropertyMetadata> ExtractInterfaceDefaultProperties(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties)
    {
        var interfaceProperties = new List<PropertyMetadata>();
        var classPropertyNames = new HashSet<string>(classProperties.Select(p => p.Name));
        var processedPropertyNames = new HashSet<string>();

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            foreach (var member in interfaceType.GetMembers())
            {
                if (member is not IPropertySymbol property)
                {
                    continue;
                }

                // Skip properties already declared in the class
                if (classPropertyNames.Contains(property.Name))
                {
                    continue;
                }

                // Skip properties already processed from another interface (diamond inheritance)
                if (processedPropertyNames.Contains(property.Name))
                {
                    continue;
                }

                // Check if property has a default implementation
                // A property has a default implementation if its getter is not abstract
                var hasDefaultImplementation = property.GetMethod != null && !property.GetMethod.IsAbstract;

                if (!hasDefaultImplementation)
                {
                    continue;
                }

                processedPropertyNames.Add(property.Name);

                var fullyQualifiedTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var accessModifier = GetAccessModifierFromAccessibility(property.DeclaredAccessibility);
                var interfaceTypeName = interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                var hasGetter = property.GetMethod != null;
                var hasSetter = property.SetMethod != null && !property.SetMethod.IsInitOnly;
                var hasInit = property.SetMethod?.IsInitOnly == true;

                // Interface default properties cannot be partial, virtual is implicit
                interfaceProperties.Add(new PropertyMetadata(
                    property.Name,
                    fullyQualifiedTypeName,
                    accessModifier,
                    IsPartial: false,
                    IsVirtual: true,  // Interface default implementations are implicitly virtual
                    IsOverride: false,
                    IsDerived: true,  // Default implementations are essentially derived properties
                    IsRequired: false,
                    hasGetter,
                    hasSetter,
                    hasInit,
                    IsFromInterface: true,
                    GetterAccessModifier: null,
                    SetterAccessModifier: null,
                    InterfaceTypeName: interfaceTypeName));
            }
        }

        return interfaceProperties;
    }

    private static string GetAccessModifierFromAccessibility(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => "public"  // Interface members default to public
        };
    }

    /// <summary>
    /// Detects the constructor state for the class.
    /// Returns a tuple of:
    /// - NeedsGeneratedParameterlessConstructor: true if no constructor exists and we need to generate one
    /// - HasOrWillHaveParameterlessConstructor: true if we have or will generate a parameterless constructor
    /// </summary>
    private static (bool NeedsGeneratedParameterlessConstructor, bool HasOrWillHaveParameterlessConstructor) DetectConstructorState(
        ClassDeclarationSyntax[] allClassDeclarations)
    {
        var firstConstructor = allClassDeclarations
            .SelectMany(c => c.Members)
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault();

        // If no constructor exists, we need to generate a parameterless one
        if (firstConstructor == null)
        {
            return (NeedsGeneratedParameterlessConstructor: true, HasOrWillHaveParameterlessConstructor: true);
        }

        // If first constructor is parameterless, we already have one
        if (firstConstructor.ParameterList.Parameters.Count == 0)
        {
            return (NeedsGeneratedParameterlessConstructor: false, HasOrWillHaveParameterlessConstructor: true);
        }

        // First constructor has parameters, so we don't have a parameterless constructor
        return (NeedsGeneratedParameterlessConstructor: false, HasOrWillHaveParameterlessConstructor: false);
    }

    private static string GetAccessModifier(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            return "public";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
            return "internal";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword)))
            return "protected";
        return "private";
    }

    private static string? GetAccessorModifier(AccessorListSyntax? accessorList, SyntaxKind accessorKind)
    {
        var accessor = accessorList?.Accessors.FirstOrDefault(a => a.IsKind(accessorKind));
        if (accessor == null)
        {
            return null;
        }

        var modifiers = accessor.Modifiers;
        if (modifiers.Count == 0)
        {
            return null;
        }

        return string.Join(" ", modifiers.Select(m => m.ValueText));
    }

    private static bool HasDerivedAttribute(PropertyDeclarationSyntax property, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return HasAttribute(property.AttributeLists, "Namotion.Interceptor.Attributes.DerivedAttribute", semanticModel, cancellationToken);
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> attributeLists, string baseTypeName, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return attributeLists
            .SelectMany(al => al.Attributes)
            .Any(attribute =>
            {
                var attributeType = semanticModel.GetTypeInfo(attribute, cancellationToken).Type as INamedTypeSymbol;
                return attributeType is not null && IsTypeOrInheritsFrom(attributeType, baseTypeName);
            });
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

    private static bool ImplementsInterface(ITypeSymbol? type, string interfaceTypeName)
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

        if (type.AllInterfaces.Any(i => i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName))
        {
            return true;
        }

        return type.BaseType is { } baseType && ImplementsInterface(baseType, interfaceTypeName);
    }

    private static bool IsTypeOrInheritsFrom(ITypeSymbol? type, string fullTypeName)
    {
        while (type is not null)
        {
            if (type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == fullTypeName)
            {
                return true;
            }

            type = type.BaseType;
        }

        return false;
    }

    private static string? GetFullTypeName(TypeSyntax? type, SemanticModel semanticModel)
    {
        if (type == null)
        {
            return null;
        }

        var typeInfo = semanticModel.GetTypeInfo(type);
        var symbol = typeInfo.Type;
        if (symbol != null)
        {
            return GetFullTypeName(symbol);
        }

        return null;
    }

    private static string? GetFullTypeName(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } namedTypeSymbol)
        {
            var genericArguments = string.Join(", ", namedTypeSymbol.TypeArguments.Select(GetFullTypeName));
            return $"{namedTypeSymbol.ContainingNamespace}.{namedTypeSymbol.Name}<{genericArguments}>";
        }

        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
