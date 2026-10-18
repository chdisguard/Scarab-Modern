namespace Scarab.Compatibility;

public static class GameVersion
{
    public static string Canonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value.Trim(), out var v) || v.Major != 1)
            throw new InvalidDataException($"Invalid Hollow Knight version: {value}");
        return v.Revision > 0 ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}.{v.Revision}" :
            $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }
    public static bool Same(string a, string b) => Canonical(a) == Canonical(b);
}
