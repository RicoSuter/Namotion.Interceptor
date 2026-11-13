using System.Collections.Immutable;
using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Virtual;

/// <summary>
/// Adds the entries of one level of a virtual subject to a registered subject as dynamic properties. Adds only what is
/// missing, so it can run on every attach: a subject that attaches again may keep its dynamic properties.
/// </summary>
internal sealed class VirtualPropertyMaterializer(VirtualRuntime runtime)
{
    /// <summary>
    /// Adds the valid entries that are not in <paramref name="rejected"/>, and collects the expression and binding
    /// properties. An invalid or rejected entry is skipped with the entries below it; the validator reports it.
    /// </summary>
    /// <param name="subject">The subject of the level.</param>
    /// <param name="entries">The entries of the level.</param>
    /// <param name="place">Where the level is.</param>
    /// <param name="prefix">The path in the tree of the level, null for the virtual subject itself.</param>
    /// <param name="rejected">The paths in the tree of the entries that read themselves through other entries.</param>
    /// <param name="computed">Gets the expression and binding properties that were added or found.</param>
    public void Apply(
        RegisteredSubject subject,
        IReadOnlyDictionary<string, VirtualProperty>? entries,
        VirtualEntryPlace place,
        string? prefix,
        ImmutableHashSet<string> rejected,
        List<PropertyReference> computed)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var (name, entry) in entries)
        {
            var path = prefix is null ? name : prefix + SubjectPathResolver.Separator + name;
            if (VirtualPropertyValidator.Check(name, entry, place) is not null || rejected.Contains(path))
            {
                continue;
            }

            try
            {
                if (entry.Properties is { } children)
                {
                    if (subject.TryGetProperty(name) is null)
                    {
                        // The node applies its entries when this assignment attaches it.
                        var node = new VirtualNode(children, path, rejected, runtime);
                        subject.AddProperty(name, typeof(VirtualNode), _ => node, null);
                    }

                    continue;
                }

                var property = subject.TryGetProperty(name)
                    ?? AddValue(entry, subject.Subject, (type, read, write, attributes) => subject.AddDerivedProperty(name, type, read, write, attributes));
                computed.Add(property.Reference);
                ApplyAttributes(property, entry.Attributes, subject.Subject, path, rejected, computed);
            }
            catch (Exception exception)
            {
                // Lifecycle handlers must not throw, and one entry must not stop the others.
                runtime.Logger.LogError(exception, "The virtual property {Path} could not be added.", path);
            }
        }
    }

    private void ApplyAttributes(
        RegisteredSubjectProperty owner,
        IReadOnlyDictionary<string, VirtualProperty>? attributes,
        IInterceptorSubject scope,
        string ownerPath,
        ImmutableHashSet<string> rejected,
        List<PropertyReference> computed)
    {
        if (attributes is null)
        {
            return;
        }

        foreach (var (name, entry) in attributes)
        {
            var path = ownerPath + "@" + name;
            if (VirtualPropertyValidator.Check(name, entry, VirtualEntryPlace.Attribute) is not null || rejected.Contains(path))
            {
                continue;
            }

            try
            {
                var attribute = owner.TryGetAttribute(name)
                    ?? AddValue(entry, scope, (type, read, write, reflectionAttributes) => owner.AddDerivedAttribute(name, type, read, write, reflectionAttributes));
                computed.Add(attribute.Reference);
                ApplyAttributes(attribute, entry.Attributes, scope, path, rejected, computed);
            }
            catch (Exception exception)
            {
                // Lifecycle handlers must not throw, and one attribute must not stop the others.
                runtime.Logger.LogError(exception, "The virtual property attribute {Path} could not be added.", path);
            }
        }
    }

    private RegisteredSubjectProperty AddValue(
        VirtualProperty entry,
        IInterceptorSubject scope,
        Func<Type, Func<IInterceptorSubject, object?>, Action<IInterceptorSubject, object?>?, Attribute[], RegisteredSubjectProperty> add)
    {
        var dataType = entry.DataType!.Value;
        var type = VirtualDataTypes.GetClrType(dataType);
        var attributes = CreateAttributes(entry.State);
        if (entry.Path is { } path)
        {
            var binding = new VirtualBinding(path, dataType, scope, runtime);
            var bindingProperty = add(type, binding.Read, binding.Write, attributes);
            binding.Misses.Attach(bindingProperty.Reference);
            return bindingProperty;
        }

        // The validator has checked that the entry has a path or an expression that parses.
        if (entry.Expression is not { } text || !VirtualExpression.TryParse(text, out var expression, out _))
        {
            throw new InvalidOperationException("The entry has no path and no valid expression.");
        }

        var evaluator = new VirtualExpressionEvaluator(expression, dataType, scope, runtime);
        var expressionProperty = add(type, evaluator.Evaluate, null, attributes);
        evaluator.Misses.Attach(expressionProperty.Reference);
        return expressionProperty;
    }

    // PropertyAttributeInitializer turns these into the State registry attribute, as for a declared property.
    private static Attribute[] CreateAttributes(VirtualState? state)
    {
        if (state is null)
        {
            return [];
        }

        var attribute = new StateAttribute { Title = state.Title };
        if (state.Unit is { } unit)
        {
            attribute.Unit = unit;
        }

        if (state.Position is { } position)
        {
            attribute.Position = position;
        }

        if (state.IsCumulative is { } isCumulative)
        {
            attribute.IsCumulative = isCumulative;
        }

        if (state.IsDiscrete is { } isDiscrete)
        {
            attribute.IsDiscrete = isDiscrete;
        }

        if (state.IsEstimated is { } isEstimated)
        {
            attribute.IsEstimated = isEstimated;
        }

        return state.Category is { } category ? [attribute, new CategoryAttribute(category)] : [attribute];
    }
}
