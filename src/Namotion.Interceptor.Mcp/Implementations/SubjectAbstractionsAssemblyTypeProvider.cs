using System.Reflection;
using Namotion.Interceptor.Mcp.Abstractions;

namespace Namotion.Interceptor.Mcp.Implementations;

/// <summary>
/// Returns interfaces from assemblies marked with <see cref="SubjectAbstractionsAssemblyAttribute"/>.
/// </summary>
public class SubjectAbstractionsAssemblyTypeProvider : IMcpTypeProvider
{
    /// <inheritdoc />
    public IEnumerable<McpTypeInfo> GetTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetCustomAttribute<SubjectAbstractionsAssemblyAttribute>() is null)
            {
                continue;
            }

            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsInterface)
                {
                    yield return new McpTypeInfo(type.FullName!, null, IsInterface: true);
                }
            }
        }
    }
}
