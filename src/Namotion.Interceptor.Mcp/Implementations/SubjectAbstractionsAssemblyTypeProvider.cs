using System.Reflection;
using Namotion.Interceptor.Mcp.Abstractions;

namespace Namotion.Interceptor.Mcp.Implementations;

/// <summary>
/// Returns interfaces from assemblies marked with <see cref="SubjectAbstractionsAssemblyAttribute"/>.
/// </summary>
public class SubjectAbstractionsAssemblyTypeProvider : IMcpTypeProvider
{
    private readonly IEnumerable<Assembly>? _assemblies;

    public SubjectAbstractionsAssemblyTypeProvider()
    {
    }

    public SubjectAbstractionsAssemblyTypeProvider(IEnumerable<Assembly> assemblies)
    {
        _assemblies = assemblies;
    }

    /// <inheritdoc />
    public IEnumerable<McpTypeInfo> GetTypes()
    {
        var assemblies = _assemblies ?? AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            if (assembly.GetCustomAttribute<SubjectAbstractionsAssemblyAttribute>() is null)
            {
                continue;
            }

            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsInterface)
                {
                    yield return new McpTypeInfo(type.FullName!, null, IsInterface: true, Type: type);
                }
            }
        }
    }
}
