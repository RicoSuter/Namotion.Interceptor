using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Namotion.Interceptor;

namespace Namotion.Proxy.AspNetCore.Controllers;

internal class SubjectControllerFeatureProvider<TController, TSubject> : IApplicationFeatureProvider<ControllerFeature>
    where TSubject : class, IInterceptorSubject
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        feature.Controllers.Add(typeof(TController).GetTypeInfo());
    }
}
