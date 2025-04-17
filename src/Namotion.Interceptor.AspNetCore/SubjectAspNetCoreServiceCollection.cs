using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Namotion.Interceptor;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Sources.Updates;
using Namotion.Interceptor.Validation;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectAspNetCoreServiceCollection
{
    public static IEndpointRouteBuilder MapSubjectApis<TSubject>(this IEndpointRouteBuilder builder,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector, 
        string path)
        where TSubject : class, IInterceptorSubject
    {
        builder
            .MapGet(path, 
                Ok<JsonObject>(HttpContext context) =>
                {
                    var subject = subjectSelector(context.RequestServices);
                    var jsonOptions = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>();
                    return TypedResults.Ok(subject.ToJsonObject(jsonOptions.Value.JsonSerializerOptions));
                })
            .Produces<TSubject>()
            .WithTags(typeof(TSubject).Name);

        builder
            .MapPost(path, 
                IResult(
                    [FromBody] Dictionary<string, JsonElement> updates,
                    [FromServices] IEnumerable<IPropertyValidator> propertyValidators,
                    HttpContext context) =>
                {
                    var subject = subjectSelector(context.RequestServices);
                    return UpdatePropertyValues(subject, updates, propertyValidators);
                })
            .WithTags(typeof(TSubject).Name);

        builder
            .MapGet(path + "/structure",
                Ok<SubjectUpdate>(HttpContext context) =>
                {
                    var subject = subjectSelector(context.RequestServices);
                    return TypedResults.Ok(SubjectUpdate
                        .CreateCompleteUpdate(subject)
                        .ConvertToJsonCamelCasePath());
                })
            .Produces<SubjectUpdate>()
            .WithTags(typeof(TSubject).Name);
        
        return builder;
    }
    
    private static IResult UpdatePropertyValues<TSubject>(
        TSubject subject,
        [FromBody] Dictionary<string, JsonElement> updates,
        [FromServices] IEnumerable<IPropertyValidator> propertyValidators)
        where TSubject : class, IInterceptorSubject
    {
        try
        {
            var resolvedUpdates = updates
                .Select(t =>
                {
                    var (subject2, property) = subject.FindPropertyFromJsonPath(t.Key);
                    return new
                    {
                        t.Key,
                        t.Value,
                        Subject = subject2,
                        Property = property
                    };
                })
                .ToArray();

            // check only known variables
            if (resolvedUpdates.Any(u => u.Subject == null))
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Detail = "Unknown property paths."
                });
            }

            // check not read-only
            if (resolvedUpdates.Any(u => u.Property.SetValue is null))
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Detail = "Attempted to change read only property."
                });
            }

            // run validators
            var errors = new Dictionary<string, ValidationResult[]>();
            var propertyValidatorsArray = propertyValidators.ToArray();
            foreach (var update in resolvedUpdates)
            {
                var updateErrors = propertyValidatorsArray
                    .SelectMany(v => v.Validate(
                        new PropertyReference(update.Subject!, update.Property.Name), update.Value))
                    .ToArray();

                if (updateErrors.Any())
                {
                    errors.Add(update.Key, updateErrors);
                }
            }

            if (errors.Any())
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Detail = "Property updates not valid.",
                    Extensions =
                    {
                        { "errors", errors.ToDictionary(e => e.Key, e => e.Value.Select(v => v.ErrorMessage)) }
                    }
                });
            }

            // write updates
            foreach (var update in resolvedUpdates)
            {
                update.Property.SetValue?.Invoke(update.Subject, update.Value.Deserialize(update.Property.Type));
            }

            return TypedResults.Ok();
        }
        catch (JsonException)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Detail = "Invalid property value."
            });
        }
    }
}

