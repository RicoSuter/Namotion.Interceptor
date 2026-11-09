using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// Resolves the paths of automation expressions and actions. An absolute path starts at the root, any other path at the
/// rule, so <c>./LastTriggeredAt</c> is a property of the rule and <c>../CurrentState</c> one of its automation. A
/// resolution reports its graph route to the recorder it is given, see <see cref="ISubjectPathRecorder"/>.
/// </summary>
internal static class AutomationPaths
{
    /// <summary>The character between the segments of a path.</summary>
    public const char Separator = '/';

    // The names that the attributes of a property of a subject without parents have on the routes, see
    // GetAttributePropertyName, kept with the property so that recording them again allocates nothing.
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, string> ConfigurationNames = new();
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, string> StateNames = new();
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, string>.CreateValueCallback CreateConfigurationName =
        property => GetAttributePropertyName(property.Name, KnownAttributes.Configuration);
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, string>.CreateValueCallback CreateStateName =
        property => GetAttributePropertyName(property.Name, KnownAttributes.State);

    /// <summary>
    /// Gets the name under which a route records the attribute of a property, the name that
    /// <see cref="RegisteredSubjectProperty.AddAttribute"/> gives it; an attribute added under another name is announced
    /// under this one too, see <see cref="Runtime.PropertyAttachNotifier"/>.
    /// </summary>
    public static string GetAttributePropertyName(string propertyName, string attributeName) => $"{propertyName}@{attributeName}";

    /// <summary>
    /// Resolves the property a path ends at, as <see cref="ResolveTarget"/> does, when an expression may read it: a secret
    /// or excluded property resolves to null.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveProperty(SubjectPathResolver resolver, string path, IInterceptorSubject rule, ISubjectPathRecorder? recorder = null)
    {
        var property = ResolveTarget(resolver, path, rule, recorder);
        return property is not null && IsReadable(property, recorder) ? property : null;
    }

    private static bool IsReadable(RegisteredSubjectProperty property, ISubjectPathRecorder? recorder)
    {
        var isReadable = property.IsDisplayable();
        if (recorder is null)
        {
            return isReadable;
        }

        // Added at run time, a secret configuration hides the property and state metadata shows a plain one.
        var configuration = property.TryGetAttribute(KnownAttributes.Configuration);
        if (configuration is null)
        {
            AddAttribute(property, ConfigurationNames, CreateConfigurationName, recorder);
        }

        if (!isReadable && configuration?.GetValue() is not ConfigurationMetadata { IsSecret: true } &&
            property.TryGetAttribute(KnownAttributes.State) is null)
        {
            AddAttribute(property, StateNames, CreateStateName, recorder);
        }

        return isReadable;
    }

    // The notifier announces such an attribute on the parents of its subject, which stay on the route when another subject
    // takes the place of this one, so that the route of the path stays the same instance then.
    private static void AddAttribute(
        RegisteredSubjectProperty property,
        ConditionalWeakTable<RegisteredSubjectProperty, string> names,
        ConditionalWeakTable<RegisteredSubjectProperty, string>.CreateValueCallback createName,
        ISubjectPathRecorder recorder)
    {
        var parents = property.Parent.Parents;
        if (parents.IsEmpty)
        {
            recorder.AddProperty(new PropertyReference(property.Subject, names.GetValue(property, createName)));
            return;
        }

        foreach (var parent in parents)
        {
            recorder.AddProperty(parent.Property.Reference);
        }
    }

    /// <summary>
    /// Resolves the property or operation a path ends at: the last segment names it, the part before it leads to its subject.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveTarget(SubjectPathResolver resolver, string path, IInterceptorSubject rule, ISubjectPathRecorder? recorder = null)
    {
        var lastSeparator = path.LastIndexOf(Separator);
        var name = lastSeparator < 0 ? path : path[(lastSeparator + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        IInterceptorSubject? owner;
        if (lastSeparator < 0)
        {
            AddRuleParents(rule, recorder);
            owner = rule;
        }
        else
        {
            owner = ResolveSubject(resolver, lastSeparator == 0 ? "/" : path[..lastSeparator], rule, recorder);
        }

        return owner is null ? null : FindProperty(owner, name, recorder);
    }

    private static RegisteredSubjectProperty? FindProperty(IInterceptorSubject owner, string name, ISubjectPathRecorder? recorder)
    {
        if (owner.TryGetRegisteredSubject() is not { } registered)
        {
            recorder?.MarkUnbounded();
            return null;
        }

        var property = registered.TryGetProperty(name);
        if (property is null)
        {
            // Added later as a dynamic property.
            recorder?.AddProperty(new PropertyReference(owner, name));
        }

        return property;
    }

    /// <summary>Resolves a subject; a path that leads nowhere or is malformed gives null.</summary>
    public static IInterceptorSubject? ResolveSubject(SubjectPathResolver resolver, string path, IInterceptorSubject rule, ISubjectPathRecorder? recorder = null)
    {
        if (!path.StartsWith(Separator))
        {
            AddRuleParents(rule, recorder);
        }

        if (path == ".")
        {
            return rule;
        }

        try
        {
            return recorder is null
                ? resolver.ResolveSubject(path, PathStyle.Canonical, rule)
                : resolver.ResolveSubject(path, PathStyle.Canonical, rule, recorder);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Written by the author of a rule: a malformed path leads nowhere instead of failing the automation.
            recorder?.MarkUnbounded();
            return null;
        }
    }

    // The resolver leaves out the attachment of the subject a relative path starts at, and a rule is detached and
    // attached again by a write of the property that holds it.
    private static void AddRuleParents(IInterceptorSubject rule, ISubjectPathRecorder? recorder)
    {
        if (recorder is null)
        {
            return;
        }

        if (rule.TryGetRegisteredSubject() is { } registered)
        {
            recorder.AddParentStep(rule, registered.Parents);
        }
        else
        {
            recorder.MarkUnbounded();
        }
    }

    /// <summary>Reads the property at a path that starts at the subject, when an expression may read it.</summary>
    public static ExpressionValue ReadFrom(SubjectPathResolver resolver, string path, IInterceptorSubject subject)
    {
        var relativePath = path.StartsWith("./", StringComparison.Ordinal) ? path : "./" + path;
        var property = ResolveProperty(resolver, relativePath, subject);
        return property is null ? ExpressionValue.Missing : ExpressionValue.FromProperty(property, property.GetValue());
    }
}
