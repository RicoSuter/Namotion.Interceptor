using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Extensions;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.AspNetCore.Controllers;

public abstract class SubjectControllerBase<TSubject> : ControllerBase
    where TSubject : IInterceptorSubject
{
    private readonly TSubject _subject;
    private readonly IOptions<JsonOptions> _jsonOptions;

    protected SubjectControllerBase(TSubject subject, IOptions<JsonOptions> jsonOptions)
    {
        _subject = subject;
        _jsonOptions = jsonOptions;
    }

    /// <summary>
    /// Gets the subject as JSON object.
    /// </summary>
    [HttpGet]
    public ActionResult<TSubject> GetSubject()
    {
        // TODO: Correctly generate OpenAPI schema
        return Ok(_subject.ToJsonObject(_jsonOptions.Value.JsonSerializerOptions));
    }

    /// <summary>
    /// Gets the subject structure with metadata.
    /// </summary>
    [HttpGet("structure")]
    public ActionResult<SubjectUpdate> GetSubjectStructure()
    {
        return Ok(SubjectUpdate
            .CreateCompleteUpdate(_subject)
            .ConvertToJsonCamelCasePath());
    }

    /// <summary>
    /// Patches the subject JSON object using JSON paths.
    /// </summary>
    [HttpPost]
    public ActionResult UpdatePropertyValues(
        [FromBody] Dictionary<string, JsonElement> updates,
        [FromServices] IEnumerable<IPropertyValidator> propertyValidators)
    {
        try
        {
            var resolvedUpdates = updates
                .Select(t =>
                {
                    var (subject, property) = _subject.FindPropertyFromJsonPath(t.Key);
                    return new
                    {
                        t.Key,
                        t.Value,
                        Subject = subject,
                        Property = property
                    };
                })
                .ToArray();

            // check only known variables
            if (resolvedUpdates.Any(u => u.Subject == null))
            {
                return BadRequest(new ProblemDetails
                {
                    Detail = "Unknown property paths."
                });
            }

            // check not read-only
            if (resolvedUpdates.Any(u => u.Property.SetValue is null))
            {
                return BadRequest(new ProblemDetails
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
                return BadRequest(new ProblemDetails
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

            return Ok();
        }
        catch (JsonException)
        {
            return BadRequest(new ProblemDetails
            {
                Detail = "Invalid property value."
            });
        }
    }
}
