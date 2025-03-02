using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.AspNetCore.Controllers;

public abstract class SubjectControllerBase<TSubject> : ControllerBase
    where TSubject : IInterceptorSubject
{
    private readonly TSubject _subject;

    protected SubjectControllerBase(TSubject subject)
    {
        _subject = subject;
    }

    [HttpGet]
    public ActionResult<TSubject> GetSubject()
    {
        // TODO: correctly generate OpenAPI schema
        return Ok(_subject.ToJsonObject());
    }

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
            foreach (var update in resolvedUpdates)
            {
                var updateErrors = propertyValidators
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

    /// <summary>
    /// Gets all leaf properties.
    /// </summary>
    /// <returns></returns>
    [HttpGet("properties")]
    public ActionResult<SubjectDescription> GetProperties()
    {
        return Ok(CreateSubjectDescription(_subject, _subject.Context.GetService<ISubjectRegistry>()));
    }

    private static SubjectDescription CreateSubjectDescription(IInterceptorSubject subject, ISubjectRegistry registry)
    {
        var description = new SubjectDescription
        {
            Type = subject.GetType().Name
        };

        if (registry.KnownSubjects.TryGetValue(subject, out var registeredSubject))
        {
            foreach (var property in registeredSubject.Properties
                .Where(p => p.Value.HasGetter &&
                            p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any() == false))
            {
                var propertyName = property.GetJsonPropertyName();
                var value = property.Value.GetValue();

                description.Properties[propertyName] = CreatePropertyDescription(registry, registeredSubject, property.Key, property.Value, value);
            }
        }

        return description;
    }

    public class SubjectDescription
    {
        public required string Type { get; init; }

        public Dictionary<string, SubjectPropertyDescription> Properties { get; } = new();
    }

    public class SubjectPropertyDescription
    {
        public required string Type { get; init; }

        public object? Value { get; internal set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public IReadOnlyDictionary<string, SubjectPropertyDescription>? Attributes { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public SubjectDescription? Subject { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public List<SubjectDescription>? Subjects { get; set; }
    }

    private static SubjectPropertyDescription CreatePropertyDescription(ISubjectRegistry registry, RegisteredSubject parent, 
        string propertyName, RegisteredSubjectProperty property, object? value)
    {
        var attributes = parent.Properties
            .Where(p => p.Value.HasGetter &&
                        p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any(a => a.PropertyName == propertyName))
            .ToDictionary(
                p => p.Value.Attributes.OfType<PropertyAttributeAttribute>().Single().AttributeName,
                p => CreatePropertyDescription(registry, parent, p.Key, p.Value, p.Value.GetValue()));

        var description = new SubjectPropertyDescription
        {
            Type = property.Type.Name,
            Attributes = attributes.Any() ? attributes : null
        };

        if (value is IInterceptorSubject childSubject)
        {
            description.Subject = CreateSubjectDescription(childSubject, registry);
        }
        else if (value is ICollection collection && collection.OfType<IInterceptorSubject>().Any())
        {
            description.Subjects = collection
                .OfType<IInterceptorSubject>()
                .Select(arrayProxyItem => CreateSubjectDescription(arrayProxyItem, registry))
                .ToList();
        }
        else
        {
            description.Value = value;
        }

        return description;
    }
}
