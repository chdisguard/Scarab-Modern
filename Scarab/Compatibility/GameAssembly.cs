using System.IO.Abstractions;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Scarab.Compatibility;

public static class GameAssembly
{
    public static string ReadVersion(byte[] assembly)
    {
        using var pe = new PEReader(new MemoryStream(assembly));
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(type.Name) != "Constants" || metadata.GetString(type.Namespace) != "") continue;
            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                if (metadata.GetString(field.Name) != "GAME_VERSION" || field.GetDefaultValue().IsNil) continue;
                var constant = metadata.GetConstant(field.GetDefaultValue());
                if (constant.TypeCode == ConstantTypeCode.String)
                    return System.Text.Encoding.Unicode.GetString(metadata.GetBlobBytes(constant.Value));
            }
        }
        throw new InvalidDataException("Assembly has no Constants.GAME_VERSION string.");
    }

    public static bool HasApi(IFileSystem fs, string path)
    {
        using var stream = fs.File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeDefinitions.Any(handle =>
        {
            var type = metadata.GetTypeDefinition(handle);
            return metadata.GetString(type.Namespace) == "Modding" && metadata.GetString(type.Name) == "ModHooks";
        });
    }
}
