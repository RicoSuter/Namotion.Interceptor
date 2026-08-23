namespace Namotion.Interceptor.Generator.Models;

internal sealed record PropertyMetadata(
    string Name,
    string FullTypeName,
    string AccessModifier,
    bool IsPartial,
    bool IsVirtual,
    bool IsOverride,
    bool IsNew,
    bool IsSealed,
    bool IsDerived,
    bool IsRequired,
    bool HasGetter,
    bool HasSetter,
    bool HasInit,
    // Whether the emitted setter routes through the synchronized structural accessor helper
    // instead of the scalar one. Decided at generation time by PropertyWriteRouting, so the scalar
    // route carries no runtime check.
    bool UsesStructuralSetter,
    bool IsFromInterface,
    string? GetterAccessModifier,
    string? SetterAccessModifier,
    string? InterfaceTypeName = null,
    string? ExplicitInterfaceTypeName = null);
