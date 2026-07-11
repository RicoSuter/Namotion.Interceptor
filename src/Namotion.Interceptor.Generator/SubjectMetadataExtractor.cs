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
                 ImplementsInterface(t, KnownTypes.IInterceptorSubject)));

        var baseClassTypeName = baseClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var baseClassHasInterceptorSubject = HasInterceptorSubjectAttribute(baseClass);

        // Check if base class has INotifyPropertyChanged
        var baseClassHasInpc = baseClassHasInterceptorSubject ||
            (classDeclaration.BaseList?.Types
                .Select(t => semanticModel.GetTypeInfo(t.Type, cancellationToken).Type as INamedTypeSymbol)
                .Any(t => t != null && ImplementsInterface(t, KnownTypes.IRaisePropertyChanged)) ?? false);

        // Every generated subject exposes a callable RaisePropertyChanged. A source-declared
        // generated base receives it in this generator run; metadata and manual bases expose it
        // through their symbols. If INPC is present only through an explicit interface method,
        // emit a protected forwarder so generated descendants keep a direct call path.
        var sourceBaseWillExposeRaise = typeSymbol.BaseType is { } sourceBase &&
            sourceBase.DeclaringSyntaxReferences.Length > 0 &&
            HasInterceptorSubjectAttribute(sourceBase);
        var needsRaisePropertyChangedForwarder = baseClassHasInpc &&
            !sourceBaseWillExposeRaise &&
            !HasCallableRaisePropertyChanged(typeSymbol);

        // Collect all partial class declarations
        var allClassDeclarations = typeSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .ToArray();

        // Collect properties from all partial declarations
        var classProperties = CollectProperties(allClassDeclarations, semanticModel, cancellationToken);

        // Collect interface properties with default implementations
        var interfaceProperties = ExtractInterfaceDefaultProperties(typeSymbol, classProperties);

        // Combine class properties with interface default properties
        var properties = classProperties.Concat(interfaceProperties).ToList();

        // Collect methods from all partial declarations
        var methods = CollectMethods(allClassDeclarations, semanticModel, cancellationToken);

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
            needsRaisePropertyChangedForwarder,
            properties,
            methods);
    }

    private static bool HasCallableRaisePropertyChanged(INamedTypeSymbol type)
    {
        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            if (current.GetMembers("RaisePropertyChanged")
                .OfType<IMethodSymbol>()
                .Any(method =>
                    !method.IsStatic &&
                    !method.IsGenericMethod &&
                    method.ReturnsVoid &&
                    method.Parameters.Length == 1 &&
                    method.Parameters[0].RefKind == RefKind.None &&
                    method.Parameters[0].Type.SpecialType == SpecialType.System_String &&
                    IsAccessibleFromDerivedSubject(method, type)))
            {
                return true;
            }
        }

        return false;
    }

    // A raiser counts as callable when the generated setter of the subject can invoke it directly.
    // Internal and private protected raisers qualify only with internals access to the declaring
    // assembly (same assembly or InternalsVisibleTo); treating them as non-callable would emit a
    // forwarder that hides the accessible base method (CS0108, a build break with warnings as errors).
    private static bool IsAccessibleFromDerivedSubject(IMethodSymbol method, INamedTypeSymbol subjectType)
    {
        return method.DeclaredAccessibility switch
        {
            Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal or Accessibility.ProtectedAndInternal =>
                SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, subjectType.ContainingAssembly) ||
                method.ContainingAssembly.GivesAccessTo(subjectType.ContainingAssembly),
            _ => false,
        };
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
        ClassDeclarationSyntax[] allClassDeclarations,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var properties = new List<PropertyMetadata>();

        // First pass: collect names of On{X}Changing/On{X}Changed partial method bodies that are
        // actually implemented (have a block or expression body), across all partial declarations.
        // Name-only matching is deliberately over-approximate: a false positive costs one redundant
        // scope around a compiler-erased call plus a per-write equality comparison in the setter;
        // a false negative would silently restore source inheritance for that hook.
        var implementedHookMethods = new HashSet<string>();
        foreach (var hookClassDecl in allClassDeclarations)
        {
            foreach (var method in hookClassDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (method.Body is not null || method.ExpressionBody is not null)
                {
                    implementedHookMethods.Add(method.Identifier.ValueText);
                }
            }
        }

        foreach (var classDecl in allClassDeclarations)
        {
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

                var hasChangingHook = implementedHookMethods.Contains($"On{propertyName}Changing");
                var hasChangedHook = implementedHookMethods.Contains($"On{propertyName}Changed");

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
                    setterAccessModifier,
                    HasChangingHook: hasChangingHook,
                    HasChangedHook: hasChangedHook));
            }
        }

        return properties;
    }

    private static IReadOnlyList<MethodMetadata> CollectMethods(
        ClassDeclarationSyntax[] allClassDeclarations,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var methods = new List<MethodMetadata>();

        foreach (var classDecl in allClassDeclarations)
        {
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

                // A property has a default implementation if any accessor is not abstract
                var hasDefaultImplementation =
                    property.GetMethod is { IsAbstract: false } ||
                    property.SetMethod is { IsAbstract: false };
                if (!hasDefaultImplementation)
                {
                    continue;
                }

                processedPropertyNames.Add(property.Name);

                var fullyQualifiedTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var accessModifier = GetAccessModifierFromAccessibility(property.DeclaredAccessibility);
                var interfaceTypeName = interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                var hasGetter = property.GetMethod != null;
                var hasSetter = property.SetMethod is { IsInitOnly: false };
                var hasInit = property.SetMethod?.IsInitOnly == true;

                // Interface default properties cannot be partial, virtual is implicit
                interfaceProperties.Add(new PropertyMetadata(
                    property.Name,
                    fullyQualifiedTypeName,
                    accessModifier,
                    IsPartial: false,
                    IsVirtual: true,  // Interface default implementations are implicitly virtual
                    IsOverride: false,
                    IsDerived: HasDerivedAttribute(property),
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
        var hasPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        var hasProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        var hasInternal = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        var hasPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));

        return (hasPublic, hasProtected, hasInternal, hasPrivate) switch
        {
            (true, _, _, _) => "public",
            (_, true, true, _) => "protected internal",
            (_, true, _, true) => "private protected",
            (_, true, _, _) => "protected",
            (_, _, true, _) => "internal",
            _ => "private"
        };
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
        return SymbolExtensions.HasAttribute(property.AttributeLists, KnownTypes.DerivedAttribute, semanticModel, cancellationToken);
    }

    private static bool HasDerivedAttribute(IPropertySymbol property)
    {
        return property.GetAttributes()
            .Any(a => SymbolExtensions.IsTypeOrInheritsFrom(a.AttributeClass, KnownTypes.DerivedAttribute));
    }

    private static bool HasInterceptorSubjectAttribute(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        return type
            .GetAttributes()
            .Any(a => SymbolExtensions.IsTypeOrInheritsFrom(a.AttributeClass, KnownTypes.InterceptorSubjectAttribute));
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

    private static string GetFullTypeName(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } namedTypeSymbol)
        {
            var genericArguments = string.Join(", ", namedTypeSymbol.TypeArguments.Select(GetFullTypeName));
            return $"{namedTypeSymbol.ContainingNamespace}.{namedTypeSymbol.Name}<{genericArguments}>";
        }

        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
