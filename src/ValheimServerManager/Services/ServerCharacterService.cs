using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ValheimServerManager.Services;

public sealed record ServerCharacterFile(string PlatformId, string CharacterName, string FileName, long Size, DateTimeOffset ModifiedAt, string Sha256);

public sealed partial class ServerCharacterService(IConfiguration configuration, ServerState state)
{
    public const long MaxProfileBytes = 2 * 1024 * 1024;
    private string CharacterPath => Path.Combine(configuration["VSM_SAVE_PATH"] ?? "/data/worlds", "characters_local");
    private string PluginPath => configuration["VSM_BEPINEX_PATH"] ?? "/data/server/BepInEx";

    public bool IsInstalled => Directory.Exists(PluginPath) && Directory.EnumerateFiles(PluginPath, "ValheimServerManager.Server.dll", SearchOption.AllDirectories).Any();
    public bool ImportAvailable => state.Status == "stopped";

    public IReadOnlyList<ServerCharacterFile> List()
    {
        if (!Directory.Exists(CharacterPath)) return [];
        return Directory.EnumerateFiles(CharacterPath, "*.fch", SearchOption.TopDirectoryOnly)
            .Select(path => (path, match: ServerProfileName().Match(Path.GetFileName(path))))
            .Where(item => item.match.Success)
            .Select(item =>
            {
                var info = new FileInfo(item.path);
                using var stream = File.OpenRead(item.path);
                return new ServerCharacterFile(
                    item.match.Groups["platform"].Value,
                    item.match.Groups["character"].Value,
                    info.Name,
                    info.Length,
                    info.LastWriteTimeUtc,
                    Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            })
            .OrderBy(item => item.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ServerCharacterFile> Import(Stream source, string uploadName, string platformId, bool overwrite, CancellationToken cancellationToken)
    {
        if (state.Status != "stopped") throw new InvalidOperationException("Stop the Valheim server before importing a server character.");
        if (!IsInstalled) throw new InvalidOperationException("Install and apply the VSM server agent before importing a profile.");
        var normalizedPlatform = NormalizeSteamId(platformId);
        var characterName = CharacterNameFromFile(uploadName);
        Directory.CreateDirectory(CharacterPath);
        var destination = Path.Combine(CharacterPath, $"{normalizedPlatform}_{characterName}.fch");
        if (File.Exists(destination) && !overwrite) throw new InvalidOperationException("A server character with this owner and name already exists.");

        var temporary = Path.Combine(CharacterPath, $".vsm-import-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxProfileBytes) throw new InvalidDataException("Character save exceeds the 2 MiB limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (total < 32) throw new InvalidDataException("Character save is empty or too small to be a native .fch profile.");
                await output.FlushAsync(cancellationToken);
            }
            ValidateNativeProfile(await File.ReadAllBytesAsync(temporary, cancellationToken));

            if (File.Exists(destination))
            {
                var backupDirectory = Path.Combine(CharacterPath, "vsm-import-backups");
                Directory.CreateDirectory(backupDirectory);
                var backup = Path.Combine(backupDirectory, $"{Path.GetFileNameWithoutExtension(destination)}.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.fch");
                File.Copy(destination, backup, false);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return List().Single(item => item.FileName == Path.GetFileName(destination));
    }

    public static string NormalizeSteamId(string platformId)
    {
        var value = platformId?.Trim() ?? "";
        if (Steam64().IsMatch(value)) return "Steam_" + value;
        if (CanonicalSteam().IsMatch(value)) return value;
        throw new ArgumentException("Owner must be a 17-digit Steam64 ID or canonical Steam_<id> value.");
    }

    public static string CharacterNameFromFile(string uploadName)
    {
        if (Path.GetFileName(uploadName) != uploadName || !string.Equals(Path.GetExtension(uploadName), ".fch", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Upload a native Valheim .fch character save.");
        var name = Path.GetFileNameWithoutExtension(uploadName);
        if (!CharacterName().IsMatch(name)) throw new InvalidDataException("The .fch filename must be the character name and may contain letters, numbers, spaces, or hyphens.");
        return name;
    }

    public static void ValidateNativeProfile(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            var payloadLength = reader.ReadInt32();
            if (payloadLength < 16 || payloadLength > bytes.Length - 8) throw new InvalidDataException("Invalid native character payload length.");
            var payload = reader.ReadBytes(payloadLength);
            var hashLength = reader.ReadInt32();
            if (hashLength != 64 || stream.Length - stream.Position != hashLength) throw new InvalidDataException("Invalid native character signature length.");
            var expected = reader.ReadBytes(hashLength);
            if (!SHA512.HashData(payload).SequenceEqual(expected)) throw new InvalidDataException("Native character signature is invalid.");
        }
        catch (EndOfStreamException) { throw new InvalidDataException("Character save is truncated."); }
    }

    [GeneratedRegex(@"^\d{17}$", RegexOptions.CultureInvariant)] private static partial Regex Steam64();
    [GeneratedRegex(@"^Steam_\d{17}$", RegexOptions.CultureInvariant)] private static partial Regex CanonicalSteam();
    [GeneratedRegex(@"^[\p{L}\p{N} -]{1,64}$", RegexOptions.CultureInvariant)] private static partial Regex CharacterName();
    [GeneratedRegex(@"^(?<platform>Steam_\d{17})_(?<character>[^_]+)\.fch$", RegexOptions.CultureInvariant)] private static partial Regex ServerProfileName();
}
