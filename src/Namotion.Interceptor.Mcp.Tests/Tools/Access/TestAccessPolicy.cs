using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

internal sealed class TestAccessPolicy : IMcpAccessPolicy
{
    private readonly Func<RegisteredSubjectProperty, string, bool> _isAllowed;

    public TestAccessPolicy(Func<RegisteredSubjectProperty, string, bool> isAllowed)
    {
        _isAllowed = isAllowed;
    }

    public static TestAccessPolicy DenyRead(params string[] memberNames) =>
        new((member, action) => action != McpAccessActions.Read || !memberNames.Contains(member.Name));

    public static TestAccessPolicy DenyWrite(params string[] memberNames) =>
        new((member, action) => action != McpAccessActions.Write || !memberNames.Contains(member.Name));

    public bool IsAllowed(RegisteredSubjectProperty member, string action) => _isAllowed(member, action);
}
