using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>The configuration of a subject as its block on a page carries it.</summary>
public static class MarkdownBlockConfiguration
{
    /// <summary>
    /// Serializes the configuration of <paramref name="subject"/> for the head line and the json block of its
    /// block: in the naming of a page, and without the properties that are slots, because a slot is the
    /// content of the block. A subject with slots must be registered.
    /// </summary>
    public static string Serialize(ConfigurableSubjectSerializer serializer, IInterceptorSubject subject)
    {
        return SlotMap.For(subject.GetType()).Named.Count == 0
            ? serializer.Serialize(subject, ConfigurationNaming.CSharpProperty)
            : serializer.Serialize(
                subject,
                static property => property.ReflectionAttributes.Any(attribute => attribute is SlotAttribute),
                ConfigurationNaming.CSharpProperty);
    }
}
