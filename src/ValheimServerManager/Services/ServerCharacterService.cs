using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ValheimServerManager.Services;

public sealed record ServerCharacterFile(string PlatformId, string CharacterName, string FileName, long Size, DateTimeOffset ModifiedAt, string Sha256);
public sealed record CharacterInventoryEdit(string Action, string? Prefab, int Quantity, int Quality, int X, int Y, JsonElement? Item, string Revision, int MaxStack = 1);
public sealed record CharacterInventory(string CharacterName, string Revision, JsonElement Items, JsonElement? Catalog, int Given = 0);

public sealed partial class ServerCharacterService(IConfiguration configuration, ServerState state)
{
    public const long MaxProfileBytes = 2 * 1024 * 1024;
    private string CharacterPath => Path.Combine(configuration["VSM_SAVE_PATH"] ?? "/data/worlds", "characters_local");
    private string PluginPath => configuration["VSM_BEPINEX_PATH"] ?? "/data/server/BepInEx";
    private readonly SemaphoreSlim _inventoryGate = new(1, 1);

    public async Task<CharacterInventory> Inventory(string fileName, CharacterInventoryEdit? edit, CancellationToken cancellationToken)
    {
        await _inventoryGate.WaitAsync(cancellationToken);
        try
        {
            var character = List().SingleOrDefault(item => string.Equals(item.FileName, fileName, StringComparison.Ordinal));
            if (character is null) throw new FileNotFoundException("Server-owned character was not found.");
            if (state.Players.Any(player => SamePlatform(player.PlatformId, character.PlatformId)))
                throw new InvalidOperationException("The player is online. Edit their live inventory or wait for them to leave.");
            if (edit is not null && !string.Equals(edit.Revision, character.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The character save changed. Refresh the inventory before editing.");
            var path = Path.Combine(CharacterPath, character.FileName);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            ValidateNativeProfile(bytes);
            var command = new { action = edit?.Action ?? "inspect", profile = Convert.ToBase64String(bytes), prefab = edit?.Prefab,
                quantity = edit?.Quantity, quality = edit?.Quality, maxStack = edit?.MaxStack, x = edit?.X, y = edit?.Y, item = edit?.Item };
            var start = new ProcessStartInfo(configuration["VSM_FCH_BRIDGE_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "fchbridge"))
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Character editor could not start.");
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(command));
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidDataException(error.Trim().Length > 0 ? error.Trim() : "Character save format is unsupported.");
            using var result = JsonDocument.Parse(output);
            var root = result.RootElement;
            var given = root.TryGetProperty("given", out var delivered) ? delivered.GetInt32() : 0;
            if (edit is not null)
            {
                if (!root.TryGetProperty("profile", out var encoded)) throw new InvalidDataException("Character editor did not return a profile.");
                var updated = Convert.FromBase64String(encoded.GetString() ?? "");
                if (updated.Length > MaxProfileBytes) throw new InvalidDataException("Edited character exceeds the size limit.");
                ValidateNativeProfile(updated);
                if (state.Players.Any(player => SamePlatform(player.PlatformId, character.PlatformId)))
                    throw new InvalidOperationException("The player joined during the edit. Refresh their live inventory.");
                await using (var current = File.OpenRead(path))
                    if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(current, cancellationToken)), character.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The character save changed during the edit. Refresh the inventory.");
                var backupDirectory = Path.Combine(CharacterPath, "vsm-admin-backups");
                Directory.CreateDirectory(backupDirectory);
                var backup = Path.Combine(backupDirectory, $"{Path.GetFileNameWithoutExtension(path)}.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.fch");
                File.Copy(path, backup, false);
                var temporary = Path.Combine(CharacterPath, $".vsm-edit-{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(temporary, updated, cancellationToken);
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                bytes = updated;
            }
            return new CharacterInventory(root.GetProperty("name").GetString() ?? character.CharacterName,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), root.GetProperty("items").Clone(),
                root.TryGetProperty("catalog", out var catalog) ? catalog.Clone() : null, given);
        }
        finally { _inventoryGate.Release(); }
    }

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

    public static bool SamePlatform(string left, string right) =>
        string.Equals(CanonicalPlatform(left), CanonicalPlatform(right), StringComparison.OrdinalIgnoreCase);

    public static string CanonicalPlatform(string platformId) =>
        Steam64().IsMatch(platformId ?? "") ? "Steam_" + platformId : platformId ?? "";

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
