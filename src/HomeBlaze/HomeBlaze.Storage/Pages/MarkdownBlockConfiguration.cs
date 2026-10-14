using HomeBlaze.Services;
using Namotion.Interceptor;

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
        var slots = SlotMap.For(subject.GetType());
        return slots.Named.Count == 0
            ? serializer.Serialize(subject, ConfigurationNaming.CSharpProperty)
            : serializer.Serialize(
                subject, slots.IsSlotProperty, ConfigurationNaming.CSharpProperty);
    }
}
