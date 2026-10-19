using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models;

[InterceptorSubject]
public partial class SetterAccessSubject
{
    public partial string? PublicSetter { get; set; }

    public partial string? InternalSetter { get; internal set; }

    public partial string? ProtectedSetter { get; protected set; }

    public partial string? ProtectedInternalSetter { get; protected internal set; }

    public partial string? PrivateProtectedSetter { get; private protected set; }

    public partial string? PrivateSetter { get; private set; }

    public partial string? InitSetter { get; init; }

    [Derived]
    public string? NoSetter => PublicSetter;

    [Derived]
    public string? DerivedWithSetter
    {
        get => PublicSetter;
        set => PublicSetter = value;
    }
}

[InterceptorSubject]
public partial class RequiredSetterSubject
{
    public required partial string Required { get; set; }
}

[InterceptorSubject]
public partial class ProtectedSetterBaseSubject
{
    public virtual partial string? Name { get; protected set; }
}

[InterceptorSubject]
public partial class GetterOverrideSubject : ProtectedSetterBaseSubject
{
    public override string? Name { get => base.Name; }
}

public interface INamedSubject
{
    string? Name { get; set; }
}

[InterceptorSubject]
public partial class ExplicitInterfaceSubject : INamedSubject
{
    private string? _name;

    string? INamedSubject.Name
    {
        get => _name;
        set => _name = value;
    }
}
