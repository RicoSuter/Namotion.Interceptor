using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.Services.Mcp;

/// <summary>
/// Returns concrete subject types from HomeBlaze's SubjectTypeRegistry.
/// </summary>
public class SubjectTypeRegistryTypeProvider : IMcpTypeProvider
{
    private readonly SubjectTypeRegistry _typeRegistry;

    public SubjectTypeRegistryTypeProvider(SubjectTypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry;
    }

    public IEnumerable<McpTypeInfo> GetTypes()
    {
        foreach (var type in _typeRegistry.RegisteredTypes)
        {
            yield return new McpTypeInfo(type.FullName!, null, IsInterface: false);
        }
    }
}
