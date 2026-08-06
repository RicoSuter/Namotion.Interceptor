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
