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

        // Use the symbol rather than the syntax modifiers: a top-level class without a modifier
        // defaults to internal, a nested one to private.
        var accessModifier = GetAccessModifierFromAccessibility(typeSymbol.DeclaredAccessibility);

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

        // Collect all partial class declarations
        var allClassDeclarations = typeSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .ToArray();

        // Collect properties from all partial declarations
        var classProperties = DeduplicateByName(CollectProperties(typeSymbol, semanticModel, cancellationToken));

        // Collect interface properties with default implementations
        var interfaceProperties = ExtractInterfaceDefaultProperties(typeSymbol, classProperties, semanticModel.Compilation);

        // Combine class properties with interface default properties
        var properties = classProperties.Concat(interfaceProperties).ToList();

        // Collect methods from all partial declarations
        var methods = CollectMethods(typeSymbol, semanticModel, cancellationToken);

        // Detect constructor state
        var (needsGeneratedParameterlessConstructor, hasOrWillHaveParameterlessConstructor) =
            DetectConstructorState(allClassDeclarations);

        return new SubjectMetadata(
            className,
            accessModifier,
            namespaceName,
            fullTypeName,
            containingTypes,
            needsGeneratedParameterlessConstructor,
            hasOrWillHaveParameterlessConstructor,
            baseClassTypeName,
            baseClassHasInterceptorSubject,
            baseClassHasInpc,
            properties,
            methods);
    }

    private static string? GetNamespace(ClassDeclarationSyntax classDeclaration)
    {
        // Walk up past containing types to find namespace
        SyntaxNode? current = classDeclaration.Parent;
        while (current is TypeDeclarationSyntax)
        {
            current = current.Parent;
        }

        // null means the global namespace: the generated file must not declare one.
        return (current as NamespaceDeclarationSyntax)?.Name.ToString() ??
               (current as FileScopedNamespaceDeclarationSyntax)?.Name.ToString();
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

                var explicitInterfaceTypeName = property.ExplicitInterfaceSpecifier is { } explicitSpecifier
                    ? declarationModel
                        .GetTypeInfo(explicitSpecifier.Name, cancellationToken)
                        .Type?
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : null;

                var accessModifier = GetAccessModifier(property.Modifiers);
                var isPartial = property.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) &&
                                property.ExplicitInterfaceSpecifier is null;
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
                    setterAccessModifier,
                    InterfaceTypeName: null,
                    ExplicitInterfaceTypeName: explicitInterfaceTypeName));
            }
        }

        return properties;
    }

    /// <summary>
    /// Two declarations can share a name when a class declares a property and also explicitly
    /// implements the same interface member. Emitting both produces duplicate dictionary keys,
    /// so the non-explicit declaration wins, matching what the runtime resolves.
    /// </summary>
    private static IReadOnlyList<PropertyMetadata> DeduplicateByName(IReadOnlyList<PropertyMetadata> properties)
    {
        var result = new List<PropertyMetadata>();
        var indexByName = new Dictionary<string, int>();

        foreach (var property in properties)
        {
            if (!indexByName.TryGetValue(property.Name, out var index))
            {
                indexByName[property.Name] = result.Count;
                result.Add(property);
                continue;
            }

            if (result[index].ExplicitInterfaceTypeName is not null &&
                property.ExplicitInterfaceTypeName is null)
            {
                result[index] = property;
            }
        }

        return result;
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

                // A method named exactly "WithoutInterceptor" would yield an empty wrapper name.
                if (fullMethodName.Length == InterceptedMethodPostfix.Length)
                {
                    continue;
                }

                // The emitter drops static, generic and by-reference shapes, and cannot route an
                // explicit interface implementation through the executor.
                if (method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
                    method.TypeParameterList is not null ||
                    method.ExplicitInterfaceSpecifier is not null ||
                    method.ParameterList.Parameters.Any(parameter => parameter.Modifiers.Any(modifier =>
                        modifier.IsKind(SyntaxKind.RefKeyword) ||
                        modifier.IsKind(SyntaxKind.OutKeyword) ||
                        modifier.IsKind(SyntaxKind.InKeyword))))
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
        IReadOnlyList<PropertyMetadata> classProperties,
        Compilation compilation)
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

                // An indexer has no usable name and is parameterised.
                if (property.IsIndexer)
                {
                    continue;
                }

                // A static property with a body is not abstract, so it passes the default
                // implementation test below, but it cannot be read from an instance.
                if (property.IsStatic)
                {
                    continue;
                }

                // For an explicit implementation, IPropertySymbol.Name is the fully qualified
                // "Namespace.IHuman.Gender". The implemented member carries the simple name, and
                // its containing type is the interface the accessor must cast through: reflection
                // on the declaring interface does not find the member, and the implemented one
                // dispatches correctly in every direction.
                var explicitImplementation = property.ExplicitInterfaceImplementations.FirstOrDefault();
                var resolvedName = explicitImplementation?.Name ?? property.Name;
                var accessorInterface = explicitImplementation?.ContainingType ?? interfaceType;

                // Roslyn reports an explicit implementation as Private regardless of the implemented
                // member's real visibility, so the accessibility that matters is the implemented
                // member's. Ask the compiler directly whether generated code (living inside
                // typeSymbol, accessing the member through a cast to accessorInterface) can reach
                // it, instead of hand-rolling the rule: a hardcoded "same assembly" premise breaks
                // as soon as the interface lives in a referenced assembly (internal and protected
                // internal members are then unreachable, CS0122/CS1540, unless InternalsVisibleTo
                // says otherwise), and passing accessorInterface as the qualifying type correctly
                // rejects protected members, which are never reachable through this cast pattern.
                var accessibilityMember = explicitImplementation ?? property;
                if (!compilation.IsSymbolAccessibleWithin(accessibilityMember, typeSymbol, accessorInterface))
                {
                    continue;
                }

                // A getter or setter can be individually less accessible than the property itself
                // (e.g. `string Probe { get; private set; }`); generated code accesses whichever
                // accessor it emits directly, so each one needs its own reachability check.
                var isGetterAccessible = accessibilityMember.GetMethod is { } accessibleGetMethod &&
                    compilation.IsSymbolAccessibleWithin(accessibleGetMethod, typeSymbol, accessorInterface);
                var isSetterAccessible = accessibilityMember.SetMethod is { } accessibleSetMethod &&
                    compilation.IsSymbolAccessibleWithin(accessibleSetMethod, typeSymbol, accessorInterface);
                if (!isGetterAccessible && !isSetterAccessible)
                {
                    continue;
                }

                // Skip properties already declared in the class
                if (classPropertyNames.Contains(resolvedName))
                {
                    continue;
                }

                // Skip properties already processed from another interface (diamond inheritance)
                if (processedPropertyNames.Contains(resolvedName))
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

                processedPropertyNames.Add(resolvedName);

                var fullyQualifiedTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var accessModifier = GetAccessModifierFromAccessibility(property.DeclaredAccessibility);
                var interfaceTypeName = accessorInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                var hasGetter = property.GetMethod != null && isGetterAccessible;
                var hasSetter = property.SetMethod is { IsInitOnly: false } && isSetterAccessible;
                var hasInit = property.SetMethod?.IsInitOnly == true && isSetterAccessible;

                // Interface default properties cannot be partial, virtual is implicit
                interfaceProperties.Add(new PropertyMetadata(
                    resolvedName,
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
