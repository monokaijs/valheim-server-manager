using System;
using System.IO;
using System.Text;

namespace ValheimServerManager.ServerSupport;

// A death must be recorded on disk before the client is allowed to create its grave.
// An interrupted death leaves the character locked instead of loading a stale inventory.
public static class DeathFence
{
    public static string MarkerPath(string characterPath) => characterPath + ".vsm-death-pending";
    public static string ReceiptPath(string characterPath) => characterPath + ".vsm-death-last";

    public static bool IsPending(string characterPath) => File.Exists(MarkerPath(characterPath));
    public static bool IsCommitted(string characterPath, string deathId) =>
        Guid.TryParseExact(deathId, "N", out _) &&
        File.Exists(ReceiptPath(characterPath)) &&
        string.Equals(File.ReadAllText(ReceiptPath(characterPath), Encoding.ASCII), deathId, StringComparison.Ordinal);

    public static bool Begin(string characterPath, string deathId)
    {
        if (!Guid.TryParseExact(deathId, "N", out _)) throw new InvalidDataException("Invalid death ID.");
        var marker = MarkerPath(characterPath);
        if (File.Exists(marker)) return Matches(characterPath, deathId);
        Directory.CreateDirectory(Path.GetDirectoryName(characterPath) ?? throw new InvalidDataException("Character path has no directory."));
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.ASCII.GetBytes(deathId);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.Move(temporary, marker);
            return true;
        }
        catch (IOException) when (File.Exists(marker))
        {
            return Matches(characterPath, deathId);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool Matches(string characterPath, string deathId) =>
        Guid.TryParseExact(deathId, "N", out _) &&
        File.Exists(MarkerPath(characterPath)) &&
        string.Equals(File.ReadAllText(MarkerPath(characterPath), Encoding.ASCII), deathId, StringComparison.Ordinal);

    public static void Complete(string characterPath, string deathId)
    {
        if (!Matches(characterPath, deathId)) throw new InvalidDataException("Death fence does not match.");
        var receipt = ReceiptPath(characterPath);
        if (File.Exists(receipt)) File.Delete(receipt);
        File.Move(MarkerPath(characterPath), receipt);
        if (IsPending(characterPath)) throw new IOException("Death fence could not be cleared.");
    }
}
