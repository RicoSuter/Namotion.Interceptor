using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Pages;

/// <summary>The configuration of a subject as its block on a page carries it.</summary>
public static class MarkdownBlockConfiguration
{
    /// <summary>
    /// Serializes the configuration of <paramref name="subject"/> for the head line and the json block of its
    /// block: in the naming of a page, and without the properties that are slots, because a slot is the
    /// content of the block.
    /// </summary>
    public static string Serialize(ConfigurableSubjectSerializer serializer, IInterceptorSubject subject)
    {
        // A subject that is not registered is a new one from a dialog: its slots are null, which is not
        // written, or empty, which a page ignores.
        return SlotMap.For(subject.GetType()).Named.Count == 0 || subject.TryGetRegisteredSubject() is null
            ? serializer.Serialize(subject, ConfigurationNaming.CSharpProperty)
            : serializer.Serialize(
                subject,
                static property => property.ReflectionAttributes.Any(attribute => attribute is SlotAttribute),
                ConfigurationNaming.CSharpProperty);
    }
}
