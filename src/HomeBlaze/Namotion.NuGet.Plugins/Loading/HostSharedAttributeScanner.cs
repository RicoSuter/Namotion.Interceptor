using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Scans assembly DLLs for the Namotion.NuGet.Plugins.HostPackage assembly metadata attribute
/// using System.Reflection.Metadata (without loading the assembly into any AssemblyLoadContext).
/// </summary>
internal static class HostSharedAttributeScanner
{
    private const string HostPackageKey = "Namotion.NuGet.Plugins.HostPackage";

    /// <summary>
    /// Returns the host identifier from the
    /// [assembly: AssemblyMetadata("Namotion.NuGet.Plugins.HostPackage", "...")] attribute,
    /// or null if the file doesn't exist, isn't a valid PE, or doesn't have the attribute.
    /// </summary>
    public static string? GetHostIdentifier(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                return null;
            }

            var metadataReader = peReader.GetMetadataReader();

            foreach (var attributeHandle in metadataReader.CustomAttributes)
            {
                var attribute = metadataReader.GetCustomAttribute(attributeHandle);

                // Check if this is an AssemblyMetadataAttribute on the assembly
                if (attribute.Parent.Kind != HandleKind.AssemblyDefinition)
                {
                    continue;
                }

                if (!IsAssemblyMetadataAttribute(metadataReader, attribute))
                {
                    continue;
                }

                // Decode the attribute's fixed arguments
                var value = attribute.DecodeValue(new AttributeTypeProvider());
                if (value.FixedArguments is [{ Value: HostPackageKey } _, { Value: string val } _])
                {
                    return val;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Scans all DLLs in the given directory for the HostPackage attribute.
    /// Returns the host identifier from the first matching DLL, or null if none match.
    /// </summary>
    public static string? GetHostIdentifierFromPackage(string packageDirectory)
    {
        var dllFiles = Directory.GetFiles(packageDirectory, "*.dll", SearchOption.AllDirectories);
        return dllFiles.Select(GetHostIdentifier).FirstOrDefault(v => v != null);
    }

    private static bool IsAssemblyMetadataAttribute(MetadataReader reader, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind == HandleKind.MemberReference)
        {
            var memberRef = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                var name = reader.GetString(typeRef.Name);
                var ns = reader.GetString(typeRef.Namespace);
                return name == "AssemblyMetadataAttribute" && ns == "System.Reflection";
            }
        }
        return false;
    }

    /// <summary>
    /// Minimal ICustomAttributeTypeProvider for decoding AssemblyMetadataAttribute arguments.
    /// </summary>
    private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<Type>
    {
        public Type GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.String => typeof(string),
            PrimitiveTypeCode.Boolean => typeof(bool),
            PrimitiveTypeCode.Int32 => typeof(int),
            _ => typeof(object),
        };

        public Type GetSystemType() => typeof(Type);
        public Type GetSZArrayType(Type elementType) => elementType.MakeArrayType();
        public Type GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => typeof(object);
        public Type GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => typeof(object);
        public Type GetTypeFromSerializedName(string name) => Type.GetType(name) ?? typeof(object);
        public PrimitiveTypeCode GetUnderlyingEnumType(Type type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(Type type) => type == typeof(Type);
    }
}
