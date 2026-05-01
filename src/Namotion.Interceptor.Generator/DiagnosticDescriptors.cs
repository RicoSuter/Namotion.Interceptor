using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

internal static class DiagnosticDescriptors
{
    private const string SubjectMethodCategory = "Namotion.Interceptor.SubjectMethod";

    public static readonly DiagnosticDescriptor DuplicateSubjectMethodName = new(
        id: "NI0001",
        title: "Duplicate [SubjectMethod] name",
        messageFormat: "Multiple methods named '{0}' are marked [SubjectMethod]. Overloads are not supported; subject methods must have unique names.",
        category: SubjectMethodCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor StaticSubjectMethod = new(
        id: "NI0002",
        title: "Static [SubjectMethod] is not supported",
        messageFormat: "Method '{0}' is marked [SubjectMethod] but is static. Subject methods must be instance methods.",
        category: SubjectMethodCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
