using System.Collections.Generic;

namespace Namotion.Interceptor.Generator.Models;

internal sealed record MethodMetadata(
    string Name,
    string FullMethodName,
    string ReturnType,
    IReadOnlyList<ParameterMetadata> Parameters);

internal sealed record ParameterMetadata(string Name, string Type);
