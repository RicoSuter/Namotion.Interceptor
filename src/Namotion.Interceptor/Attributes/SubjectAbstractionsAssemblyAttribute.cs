namespace Namotion.Interceptor.Attributes;

/// <summary>
/// Marks an assembly as containing subject abstraction interfaces eligible for
/// type discovery and dynamic subject proxy interface resolution.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class SubjectAbstractionsAssemblyAttribute : Attribute;
