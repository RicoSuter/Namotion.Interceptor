using System.Reflection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Authorization.Abstractions.Tests;

public class FixedPermissionsTests
{
    [Fact]
    public void WhenEveryQuestionIsAskedOfDenyAll_ThenEachIsDenied()
    {
        // Act
        var answers = AskEveryQuestion(DenyAllPermissions.Instance);

        // Assert
        Assert.NotEmpty(answers);
        Assert.All(answers, answer => Assert.False(answer.Value, answer.Key));
    }

    [Fact]
    public void WhenEveryQuestionIsAskedOfUnrestricted_ThenEachIsAllowed()
    {
        // Act
        var answers = AskEveryQuestion(UnrestrictedPermissions.Instance);

        // Assert
        Assert.NotEmpty(answers);
        Assert.All(answers, answer => Assert.True(answer.Value, answer.Key));
    }

    [Fact]
    public void WhenDenyAllOrUnrestrictedIsAskedForRoleNames_ThenTheyNameNone()
    {
        // Act
        var denyAllNames = AskEveryQuestion(DenyAllPermissions.Instance, names: true);
        var unrestrictedNames = AskEveryQuestion(UnrestrictedPermissions.Instance, names: true);

        // Assert
        Assert.NotEmpty(denyAllNames);
        Assert.All(denyAllNames, answer => Assert.True(answer.Value, answer.Key));
        Assert.All(unrestrictedNames, answer => Assert.True(answer.Value, answer.Key));
    }

    // Calls every method of IUserPermissions with a registered member, its subject or a set of roles; a method that takes
    // anything else, or answers with anything but a decision or role names, fails the test, so that a new question is
    // answered here as well. GetValue gives a value, not a decision, and is asked only for a member CanRead allows. With
    // names, gives for each method answering with role names whether it named none.
    private static Dictionary<string, bool> AskEveryQuestion(IUserPermissions permissions, bool names = false)
    {
        var subject = new FixedPermissionsTestSubject(InterceptorSubjectContext.Create().WithRegistry());
        var registered = subject.TryGetRegisteredSubject()!;
        var member = registered.TryGetProperty(nameof(FixedPermissionsTestSubject.Value))!;

        var answers = new Dictionary<string, bool>();
        foreach (var method in typeof(IUserPermissions).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(method => !method.IsSpecialName && method.Name != nameof(IUserPermissions.GetValue)))
        {
            var arguments = method.GetParameters().Select(parameter => parameter.ParameterType switch
            {
                var type when type == typeof(RegisteredSubjectProperty) => (object)member,
                var type when type == typeof(RegisteredSubject) => registered,
                var type when type == typeof(IReadOnlyCollection<string>) => new[] { "reader" },
                var type => throw new InvalidOperationException($"{method.Name} takes a {type.Name}, which this test cannot give.")
            }).ToArray();

            var answer = method.Invoke(permissions, arguments);
            if (answer is bool decision && !names)
            {
                answers[method.Name] = decision;
            }
            else if (answer is IReadOnlyList<string> roleNames && names)
            {
                answers[method.Name] = roleNames.Count == 0;
            }
            else if (answer is not (bool or IReadOnlyList<string>))
            {
                throw new InvalidOperationException($"{method.Name} answers with a {method.ReturnType.Name}, which this test cannot check.");
            }
        }

        return answers;
    }
}

[InterceptorSubject]
public partial class FixedPermissionsTestSubject
{
    public partial int Value { get; set; }
}
