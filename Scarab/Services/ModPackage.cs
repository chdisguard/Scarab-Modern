using System.IO.Compression;

namespace Scarab.Services;

public static class ModPackage
{
    public const string OwnershipFile = ".scarab-mod-files.json";
    public static string SafeRelative(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Пустое имя файла или мода.");
        string normal = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normal) || normal.StartsWith('/') || normal.Contains(':') ||
            normal.TrimEnd('/').Split('/').Any(p => p.Length == 0 || p is ".." or "." || p.IndexOfAny(new[] { '\0', '<', '>', '|', '"', '?', '*' }) >= 0))
            throw new InvalidDataException($"Недопустимый путь в архиве мода: {name}");
        foreach (string part in normal.TrimEnd('/').Split('/'))
        {
            string device = part.Split('.')[0].ToUpperInvariant();
            if (part.EndsWith('.') || part.EndsWith(' ') || device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '1' and <= '9')
                throw new InvalidDataException($"Непереносимое имя файла в архиве: {name}");
        }
        return normal;
    }
    public static void ValidateName(string name)
    {
        if (SafeRelative(name).Contains('/') || name.StartsWith('.') || name.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException($"Недопустимое имя мода: {name}");
    }
    public static Dictionary<string, byte[]> Read(ArraySegment<byte> data, string filename)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] bytes = data.ToArray();
        bool zip = bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K';
        bool dll = bytes.Length >= 2 && bytes[0] == 'M' && bytes[1] == 'Z';
        if (!zip && !dll) throw new InvalidDataException("Сервер вернул не ZIP и не DLL. Возможно, вместо файла загружена HTML-страница ошибки.");
        if (dll)
        {
            string name = Path.GetFileName(filename.Replace('\\', '/'));
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = "Mod.dll";
            SafeRelative(name);
            files.Add(name, bytes);
            return files;
        }
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            string name = SafeRelative(entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Символические ссылки в архиве мода не поддерживаются.");
            if (name.EndsWith('/')) continue;
            if (name.Equals(OwnershipFile, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive contains a reserved manager file.");
            size += entry.Length;
            if (size > 512L * 1024 * 1024 || files.Count >= 8192) throw new InvalidDataException("Архив мода превышает лимит распаковки.");
            using var input = entry.Open();
            using var output = new MemoryStream();
            byte[] buffer = new byte[81920];
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + n > entry.Length) throw new InvalidDataException("Некорректный размер файла в ZIP.");
                output.Write(buffer, 0, n);
            }
            if (!files.TryAdd(name, output.ToArray())) throw new InvalidDataException($"Повторяющийся файл в ZIP: {name}");
        }
        if (files.Count == 0) throw new InvalidDataException("Архив мода пуст.");
        return files;
    }
}
