using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Every rule must also be listed in AnalyzerReleases.Unshipped.md, or RS2008 fails the build.
/// </summary>
internal static class Diagnostics
{
    public const string Category = "Namotion.Interceptor";

    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "NI0001",
        title: "Interceptor subject must be partial",
        messageFormat: "Interceptor subject '{0}' must be declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator emits the subject's implementation as a second partial declaration.");

    public static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        id: "NI0002",
        title: "Containing type of an interceptor subject must be partial",
        messageFormat: "Containing type '{0}' of interceptor subject '{1}' must be declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated file re-declares every containing type.");

    public static readonly DiagnosticDescriptor UnsupportedTypeKind = new(
        id: "NI0003",
        title: "InterceptorSubject is only supported on classes",
        messageFormat: "'{0}' is a {1}, and InterceptorSubject is only supported on classes",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Records and record structs are excluded because the generated plumbing breaks value equality and with-expressions.");

    public static readonly DiagnosticDescriptor GeneratorFailed = new(
        id: "NI0004",
        title: "Interceptor subject generation failed",
        // One sentence with no trailing period, because RS1032 rejects anything else, and the
        // exception message interpolated at {2} usually ends in a period of its own. The generated
        // source is only a file on disk when the consumer sets EmitCompilerGeneratedFiles, so the
        // message must not send them looking for one that is not there.
        messageFormat: "Generating '{0}' failed with {1}: {2} (the full stack trace is in the generated source, which reaches disk only with EmitCompilerGeneratedFiles set)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An unhandled exception in the generator. Please report it.");

    public static readonly DiagnosticDescriptor ShadowsBaseImplementation = new(
        id: "NI0005",
        title: "Property re-declares a member already implemented by the base class",
        messageFormat: "'{0}' re-declares '{1}', which the base class already implements, so the subject and the interface report different values",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reading through the interface resolves to the base class implementation, not this property.");

    public static readonly DiagnosticDescriptor MemberSkipped = new(
        id: "NI0006",
        title: "Unsupported member skipped",
        messageFormat: "'{0}' was skipped because {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The member is not part of the subject's properties.");

    public static readonly DiagnosticDescriptor ExplicitImplementationAttributesIgnored = new(
        id: "NI0007",
        title: "Attributes on an explicit interface implementation are ignored",
        messageFormat: "Attributes on the explicit implementation of '{0}' are absent from the subject's property metadata; an attribute the library reads, such as Derived or a validation attribute, belongs on the interface member",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Property metadata reflects the interface member, so an attribute the library reads only takes effect when it is declared there. An implementation-local attribute such as SuppressMessage or ExcludeFromCodeCoverage keeps its usual meaning on the implementation and is simply not part of the metadata.");

    public static readonly DiagnosticDescriptor PropertyNameCollision = new(
        id: "NI0008",
        title: "More than one member provides the same property name",
        // The winner and the dropped member are both interpolated: several interfaces colliding on
        // one name otherwise produce byte-identical warnings at one location, and a class-declared
        // property that takes the name is neither of the colliding members.
        messageFormat: "'{0}' is provided by more than one member; the subject exposes {1} and {2} is unreachable",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Subject properties are keyed by simple name, so only one of the colliding members is reachable. A class-declared property always takes the name; between interface members, the first one the generator reaches takes it.");

    public static readonly DiagnosticDescriptor GenericTypeNotSupported = new(
        id: "NI0009",
        title: "Generic interceptor subjects are not supported",
        messageFormat: "Interceptor subject '{0}' is generic, which is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated declaration does not carry type parameters or constraints.");

    /// <summary>
    /// Reported instead of <see cref="GenericTypeNotSupported"/> when the subject itself is not
    /// generic but a containing type is: Roslyn's <c>INamedTypeSymbol.IsGenericType</c> is true for
    /// a non-generic type nested inside a generic one, so the subject cannot be blamed by name here.
    /// </summary>
    public static readonly DiagnosticDescriptor GenericContainingTypeNotSupported = new(
        id: "NI0009",
        title: "Generic interceptor subjects are not supported",
        messageFormat: "Interceptor subject '{0}' is nested in generic containing type '{1}', which is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated declaration does not carry type parameters or constraints, and neither can any containing type.");

    public static readonly DiagnosticDescriptor FileTypeNotSupported = new(
        id: "NI0010",
        title: "File-local interceptor subjects are not supported",
        messageFormat: "Interceptor subject '{0}' is file-local, which is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A generated partial declaration cannot join a file-local type.");
}
