using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace HomeBlaze.OpcUa;

public class HomeBlazeOpcUaTypeResolver : OpcUaTypeResolver
{
    public HomeBlazeOpcUaTypeResolver(ILogger logger) : base(logger)
    {
    }

    public override Attribute[] GetDynamicPropertyAttributes(ReferenceDescription reference, ISession session)
    {
        return [..base.GetDynamicPropertyAttributes(reference, session), new StateAttribute()];
    }
}
