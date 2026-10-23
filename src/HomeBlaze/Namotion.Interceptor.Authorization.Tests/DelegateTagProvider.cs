using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization.Tests;

internal sealed class DelegateTagProvider(Func<RegisteredSubjectProperty, string[]> getTags) : IMemberTagProvider
{
    public IReadOnlyCollection<string> GetTags(RegisteredSubjectProperty member) => getTags(member);
}

internal static class TestTags
{
    /// <summary>Attributes are tagged attribute, subject-holding properties subjects, Password state and secret, every other member state.</summary>
    public static readonly IMemberTagProvider Default = new DelegateTagProvider(GetDefaultTags);

    private static string[] GetDefaultTags(RegisteredSubjectProperty member)
    {
        if (member.IsAttribute)
        {
            return ["attribute"];
        }

        if (member.CanContainSubjects)
        {
            return ["subjects"];
        }

        return member.Name == "Password" ? ["state", "secret"] : ["state"];
    }
}
