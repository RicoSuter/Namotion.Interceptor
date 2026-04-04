using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Scans assembly DLLs for the Namotion.NuGet.Plugins.HostShared assembly metadata attribute
/// using System.Reflection.Metadata (without loading the assembly into any AssemblyLoadContext).
/// </summary>
internal static class HostSharedAttributeScanner
{
    private const string HostSharedKey = "Namotion.NuGet.Plugins.HostShared";

    /// <summary>
    /// Checks whether the DLL at the given path has
    /// [assembly: AssemblyMetadata("Namotion.NuGet.Plugins.HostShared", "true")].
    /// Returns false if the file doesn't exist, isn't a valid PE, or doesn't have the attribute.
    /// </summary>
    public static bool IsHostShared(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                return false;
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
                if (value.FixedArguments.Length == 2 &&
                    value.FixedArguments[0].Value is string key &&
                    value.FixedArguments[1].Value is string val &&
                    key == HostSharedKey &&
                    string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Scans all DLLs in the given directory for the HostShared attribute.
    /// Returns true if any DLL has the attribute.
    /// </summary>
    public static bool IsAnyAssemblyHostShared(string packageDirectory)
    {
        var dllFiles = Directory.GetFiles(packageDirectory, "*.dll", SearchOption.AllDirectories);
        return dllFiles.Any(IsHostShared);
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
