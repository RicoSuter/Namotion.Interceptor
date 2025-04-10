using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Namotion.Interceptor;
using Namotion.Interceptor.AspNetCore.Controllers;
using Namotion.Interceptor.AspNetCore.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectAspNetCoreServiceCollection
{
    public static IEndpointRouteBuilder MapSubjectApis(this IEndpointRouteBuilder builder,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector, 
        string path)
    {
        // TODO: Move implementation from controller to this method
        builder
            .MapGet(path, 
                Ok<JsonObject>(HttpContext context) =>
                {
                    var subject = subjectSelector(context.RequestServices);
                    var jsonOptions = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>();
                    return TypedResults.Ok(subject.ToJsonObject(jsonOptions.Value.JsonSerializerOptions));
                });

        return builder;
    }
    
    /// <summary>
    /// Registers a generic controller with the signature 'SubjectController{TSubject} : SubjectControllerBase{TSubject} where TSubject : class'.
    /// </summary>
    /// <typeparam name="TController">The controller type.</typeparam>
    /// <typeparam name="TSubject">The subject type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddSubjectController<TSubject, TController>(this IServiceCollection services)
        where TController : SubjectControllerBase<TSubject>
        where TSubject : class, IInterceptorSubject
    {
        services
            .AddControllers()
            .ConfigureApplicationPartManager(setup =>
            {
                setup.FeatureProviders.Add(new SubjectControllerFeatureProvider<TController, TSubject>());
            });

        return services;
    }
}

