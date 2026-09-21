namespace ValheimServerManager.Services;

internal static class ArchiveSafety
{
    public static string NormalizeEntry(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0) throw new InvalidDataException("Package contains an unsafe path.");
        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':') ||
            normalized.Split('/').Any(segment => segment == ".."))
            throw new InvalidDataException("Package contains an unsafe path.");
        return normalized.TrimStart('/');
    }

    public static bool IsSymbolicLink(int externalAttributes) => ((externalAttributes >> 16) & 0xF000) == 0xA000;
}
