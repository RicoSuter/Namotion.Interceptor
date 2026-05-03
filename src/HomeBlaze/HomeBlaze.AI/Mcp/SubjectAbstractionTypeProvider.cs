using System.ComponentModel;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Returns interfaces marked with <see cref="SubjectAbstractionAttribute"/>.
/// </summary>
public class SubjectAbstractionTypeProvider : IMcpTypeProvider
{
    private readonly IEnumerable<Assembly>? _assemblies;

    public SubjectAbstractionTypeProvider()
    {
    }

    public SubjectAbstractionTypeProvider(IEnumerable<Assembly> assemblies)
    {
        _assemblies = assemblies;
    }

    /// <inheritdoc />
    public IEnumerable<McpTypeInfo> GetTypes()
    {
        var assemblies = _assemblies ?? AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsInterface && type.GetCustomAttribute<SubjectAbstractionAttribute>() is not null)
                {
                    var description = type.GetCustomAttribute<DescriptionAttribute>()?.Description;
                    yield return new McpTypeInfo(type.FullName!, description, IsInterface: true, Type: type);
                }
            }
        }
    }
}
