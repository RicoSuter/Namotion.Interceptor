using System.Collections.Generic;

namespace Namotion.Interceptor.Generator.Models;

internal sealed record SubjectMetadata(
    string ClassName,
    string AccessModifier,
    bool IsSealed,
    string? NamespaceName,
    string FullTypeName,
    ContainingType[] ContainingTypes,
    bool NeedsGeneratedParameterlessConstructor,
    bool HasOrWillHaveParameterlessConstructor,
    string? BaseClassTypeName,
    bool BaseClassHasInterceptorSubject,
    bool BaseClassHasInpc,
    IReadOnlyList<PropertyMetadata> Properties,
    IReadOnlyList<MethodMetadata> Methods);
