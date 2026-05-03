using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Namotion.NuGet.Plugins.Loading;
using Xunit;

namespace Namotion.NuGet.Plugins.Tests.Loading;

public class HostSharedAttributeScannerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    [Fact]
    public void WhenAssemblyHasHostPackageAttribute_ThenReturnsHostIdentifier()
    {
        // Arrange
        var dllPath = CreateAssemblyWithMetadata("Namotion.NuGet.Plugins.HostPackage", "MyHost");

        // Act
        var result = HostSharedAttributeScanner.GetHostIdentifier(dllPath);

        // Assert
        Assert.Equal("MyHost", result);
    }

    [Fact]
    public void WhenAssemblyDoesNotHaveAttribute_ThenReturnsNull()
    {
        // Arrange
        var dllPath = CreateAssemblyWithoutMetadata();

        // Act
        var result = HostSharedAttributeScanner.GetHostIdentifier(dllPath);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenAssemblyHasAttributeWithAnyValue_ThenReturnsValue()
    {
        // Arrange
        var dllPath = CreateAssemblyWithMetadata("Namotion.NuGet.Plugins.HostPackage", "AnotherHost");

        // Act
        var result = HostSharedAttributeScanner.GetHostIdentifier(dllPath);

        // Assert
        Assert.Equal("AnotherHost", result);
    }

    [Fact]
    public void WhenAssemblyHasAttributeWithWrongKey_ThenReturnsNull()
    {
        // Arrange
        var dllPath = CreateAssemblyWithMetadata("SomeOther.Key", "MyHost");

        // Act
        var result = HostSharedAttributeScanner.GetHostIdentifier(dllPath);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenFileDoesNotExist_ThenReturnsNull()
    {
        // Act
        var result = HostSharedAttributeScanner.GetHostIdentifier("/nonexistent/path.dll");

        // Assert
        Assert.Null(result);
    }

    private string CreateAssemblyWithMetadata(string key, string value)
    {
        return BuildMinimalAssembly(assemblyMetadata: (key, value));
    }

    private string CreateAssemblyWithoutMetadata()
    {
        return BuildMinimalAssembly(assemblyMetadata: null);
    }

    /// <summary>
    /// Hand-crafts a minimal PE/DLL using System.Reflection.Metadata,
    /// optionally embedding an [assembly: AssemblyMetadata(key, value)] attribute.
    /// </summary>
    private string BuildMinimalAssembly((string Key, string Value)? assemblyMetadata)
    {
        var metadata = new MetadataBuilder();

        // Assembly row
        metadata.AddAssembly(
            name: metadata.GetOrAddString($"TestAssembly_{Guid.NewGuid():N}"),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: AssemblyFlags.PublicKey,
            hashAlgorithm: AssemblyHashAlgorithm.Sha1);

        // Module row
        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString("TestModule.dll"),
            mvid: metadata.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);

        // Reference to mscorlib / System.Runtime so the assembly is valid
        var mscorlibRef = metadata.AddAssemblyReference(
            name: metadata.GetOrAddString("System.Runtime"),
            version: new Version(9, 0, 0, 0),
            culture: default,
            publicKeyOrToken: default,
            flags: default,
            hashValue: default);

        if (assemblyMetadata.HasValue)
        {
            // Build a reference to System.Reflection.AssemblyMetadataAttribute
            var attributeTypeRef = metadata.AddTypeReference(
                resolutionScope: mscorlibRef,
                @namespace: metadata.GetOrAddString("System.Reflection"),
                name: metadata.GetOrAddString("AssemblyMetadataAttribute"));

            // Build the constructor signature: .ctor(string, string)
            var constructorSignature = new BlobBuilder();
            new BlobEncoder(constructorSignature)
                .MethodSignature(isInstanceMethod: true)
                .Parameters(2,
                    returnType => returnType.Void(),
                    parameters =>
                    {
                        parameters.AddParameter().Type().String();
                        parameters.AddParameter().Type().String();
                    });

            var constructorRef = metadata.AddMemberReference(
                parent: attributeTypeRef,
                name: metadata.GetOrAddString(".ctor"),
                signature: metadata.GetOrAddBlob(constructorSignature));

            // Encode the attribute value blob: prolog (0x0001), then two PackedLen strings, then named-arg count (0x0000)
            var attributeBlob = new BlobBuilder();
            // Fixed-arg prolog
            attributeBlob.WriteUInt16(0x0001);
            // First fixed arg: the key string
            WriteSerializedString(attributeBlob, assemblyMetadata.Value.Key);
            // Second fixed arg: the value string
            WriteSerializedString(attributeBlob, assemblyMetadata.Value.Value);
            // NumNamed = 0
            attributeBlob.WriteUInt16(0x0000);

            metadata.AddCustomAttribute(
                parent: EntityHandle.AssemblyDefinition,
                constructor: constructorRef,
                value: metadata.GetOrAddBlob(attributeBlob));
        }

        // A minimal type def (required for valid metadata): <Module>
        metadata.AddTypeDefinition(
            attributes: default,
            @namespace: default,
            name: metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        // Build the PE image
        var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll);
        var peBuilder = new ManagedPEBuilder(
            header: peHeaderBuilder,
            metadataRootBuilder: new MetadataRootBuilder(metadata),
            ilStream: new BlobBuilder());

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);

        var path = Path.Combine(Path.GetTempPath(), $"TestAssembly_{Guid.NewGuid():N}.dll");
        using (var fileStream = File.Create(path))
        {
            peBlob.WriteContentTo(fileStream);
        }

        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Writes a SerString (ECMA-335 II.23.3 custom attribute blob format):
    /// PackedLen-prefixed UTF-8 string.
    /// </summary>
    private static void WriteSerializedString(BlobBuilder builder, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteCompressedInteger(builder, bytes.Length);
        builder.WriteBytes(bytes);
    }

    /// <summary>
    /// Writes a compressed unsigned integer per ECMA-335 II.23.2.
    /// </summary>
    private static void WriteCompressedInteger(BlobBuilder builder, int value)
    {
        if (value <= 0x7F)
        {
            builder.WriteByte((byte)value);
        }
        else if (value <= 0x3FFF)
        {
            builder.WriteByte((byte)(0x80 | (value >> 8)));
            builder.WriteByte((byte)(value & 0xFF));
        }
        else
        {
            builder.WriteByte((byte)(0xC0 | (value >> 24)));
            builder.WriteByte((byte)((value >> 16) & 0xFF));
            builder.WriteByte((byte)((value >> 8) & 0xFF));
            builder.WriteByte((byte)(value & 0xFF));
        }
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
