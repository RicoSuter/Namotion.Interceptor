﻿using System.Collections;
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
    public ActionResult<TSubject> GetVariables()
    {
        // TODO: correctly generate OpenAPI schema
        return Ok(_subject.ToJsonObject());
    }

    [HttpPost]
    public ActionResult UpdateVariables(
        [FromBody] Dictionary<string, JsonElement> updates,
        [FromServices] IEnumerable<IPropertyValidator> propertyValidators)
    {
        try
        {
            var resolvedUpdates = updates
                .Select(t =>
                {
                    var (proxy, property) = _subject.FindPropertyFromJsonPath(t.Key);
                    return new
                    {
                        t.Key,
                        t.Value,
                        Proxy = proxy,
                        Property = property
                    };
                })
                .ToArray();

            // check only known variables
            if (resolvedUpdates.Any(u => u.Proxy == null))
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
                        new PropertyReference(update.Proxy!, update.Property.Name), update.Value))
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
                update.Property.SetValue?.Invoke(update.Proxy, update.Value.Deserialize(update.Property.Type));
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
    public ActionResult<ProxyDescription> GetProperties()
    {
        return Ok(CreateProxyDescription(_subject, _subject.Context.GetService<ISubjectRegistry>()));
    }

    private static ProxyDescription CreateProxyDescription(IInterceptorSubject subject, ISubjectRegistry registry)
    {
        var description = new ProxyDescription
        {
            Type = subject.GetType().Name
        };

        if (registry.KnownSubjects.TryGetValue(subject, out var metadata))
        {
            foreach (var property in metadata.Properties
                .Where(p => p.Value.HasGetter &&
                            p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any() == false))
            {
                var propertyName = property.GetJsonPropertyName();
                var value = property.Value.GetValue();

                description.Properties[propertyName] = CreateDescription(registry, metadata, property.Key, property.Value, value);
            }
        }

        return description;
    }

    public class ProxyDescription
    {
        public required string Type { get; init; }

        public Dictionary<string, ProxyPropertyDescription> Properties { get; } = new();
    }

    public class ProxyPropertyDescription
    {
        public required string Type { get; init; }

        public object? Value { get; internal set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public IReadOnlyDictionary<string, ProxyPropertyDescription>? Attributes { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ProxyDescription? Proxy { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public List<ProxyDescription>? Proxies { get; set; }
    }

    private static ProxyPropertyDescription CreateDescription(ISubjectRegistry registry, RegisteredSubject parent, 
        string propertyName, RegisteredSubjectProperty property, object? value)
    {
        var attributes = parent.Properties
            .Where(p => p.Value.HasGetter &&
                        p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any(a => a.PropertyName == propertyName))
            .ToDictionary(
                p => p.Value.Attributes.OfType<PropertyAttributeAttribute>().Single().AttributeName,
                p => CreateDescription(registry, parent, p.Key, p.Value, p.Value.GetValue()));

        var description = new ProxyPropertyDescription
        {
            Type = property.Type.Name,
            Attributes = attributes.Any() ? attributes : null
        };

        if (value is IInterceptorSubject childProxy)
        {
            description.Proxy = CreateProxyDescription(childProxy, registry);
        }
        else if (value is ICollection collection && collection.OfType<IInterceptorSubject>().Any())
        {
            var children = new List<ProxyDescription>();
            foreach (var arrayProxyItem in collection.OfType<IInterceptorSubject>())
            {
                children.Add(CreateProxyDescription(arrayProxyItem, registry));
            }
            description.Proxies = children;
        }
        else
        {
            description.Value = value;
        }

        return description;
    }
}
