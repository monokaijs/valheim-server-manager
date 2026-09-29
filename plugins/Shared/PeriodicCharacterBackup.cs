using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValheimServerManager.ServerSupport;

public static class PeriodicCharacterBackup
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public const int Retained = 5;

    public static string Folder(string characterPath) => Path.Combine(
        Path.GetDirectoryName(characterPath) ?? throw new InvalidDataException("Character path has no directory."),
        "vsm-periodic-backups");

    public static string[] Files(string characterPath)
    {
        var folder = Folder(characterPath);
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        var stem = Path.GetFileNameWithoutExtension(characterPath);
        var pattern = "^" + Regex.Escape(stem) + @"\.\d{8}-\d{6}-\d{3}\.[0-9a-f]{32}\.fch$";
        return Directory.EnumerateFiles(folder, stem + ".*.fch", SearchOption.TopDirectoryOnly)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool SaveIfDue(string characterPath, byte[] profile, DateTime utcNow)
    {
        if (profile == null || profile.Length == 0) throw new InvalidDataException("Character profile is empty.");
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("Backup time must be UTC.", nameof(utcNow));
        var existing = Files(characterPath);
        if (existing.Length > 0 && utcNow - File.GetLastWriteTimeUtc(existing[0]) < Interval)
        {
            Prune(existing);
            return false;
        }
        var folder = Folder(characterPath);
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(characterPath);
        var destination = Path.Combine(folder, stem + "." + utcNow.ToString("yyyyMMdd-HHmmss-fff") + "." + Guid.NewGuid().ToString("N") + ".fch");
        var temporary = destination + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(profile, 0, profile.Length);
                stream.Flush(true);
            }
            File.Move(temporary, destination);
            File.SetLastWriteTimeUtc(destination, utcNow);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Prune(Files(characterPath));
        return true;
    }

    private static void Prune(string[] files)
    {
        foreach (var expired in files.Skip(Retained)) File.Delete(expired);
    }
}
