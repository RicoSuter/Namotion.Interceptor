using HomeBlaze.Services;
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.AI.Mcp;

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
            if (string.IsNullOrEmpty(type.FullName))
                continue;
           
            yield return new McpTypeInfo(type.FullName, null, IsInterface: false, Type: type);
        }
    }
}
