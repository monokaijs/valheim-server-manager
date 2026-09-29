using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using ValheimServerManager.Models;
using ValheimServerManager.ServerSupport;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class CharacterBackupServiceTests
{
    [Fact]
    public async Task RestoreValidBackupChecksRevisionAndKeepsCurrentSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-restore-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var characters = Path.Combine(root, "characters_local");
            Directory.CreateDirectory(characters);
            const string fileName = "Steam_76561198400688240_Hero.fch";
            var path = Path.Combine(characters, fileName);
            var original = Profile(1);
            var changed = Profile(2);
            await File.WriteAllBytesAsync(path, original);
            Assert.True(PeriodicCharacterBackup.SaveIfDue(path, original, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VSM_SAVE_PATH"] = root }).Build();
            var state = new ServerState(null!);
            var service = new ServerCharacterService(configuration, state);
            await File.WriteAllBytesAsync(Path.Combine(characters, "PlayFab_ABC123_Other.fch"), original);
            Assert.Contains(service.List(), character => character.PlatformId == "PlayFab_ABC123" && character.CharacterName == "Other");
            var initial = service.Backups(fileName);
            Assert.Single(initial.Backups);
            await File.WriteAllBytesAsync(path, changed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreBackup(fileName, initial.Backups[0].FileName,
                new CharacterBackupRestore(initial.Revision, initial.Backups[0].Sha256), CancellationToken.None));
            var current = service.Backups(fileName);
            await File.WriteAllTextAsync(path + ".vsm-death-pending", Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreBackup(fileName, current.Backups[0].FileName,
                new CharacterBackupRestore(current.Revision, current.Backups[0].Sha256), CancellationToken.None));
            File.Delete(path + ".vsm-death-pending");
            await Assert.ThrowsAsync<FileNotFoundException>(() => service.RestoreBackup(fileName, "../other.fch",
                new CharacterBackupRestore(current.Revision, current.Backups[0].Sha256), CancellationToken.None));
            var players = (ConcurrentDictionary<long, PlayerInfo>)typeof(ServerState).GetField("_players", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
            players[1] = new PlayerInfo(1, "Hero", "Steam_76561198400688240", DateTimeOffset.UtcNow, null, true, true, true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreBackup(fileName, current.Backups[0].FileName,
                new CharacterBackupRestore(current.Revision, current.Backups[0].Sha256), CancellationToken.None));
            players.Clear();
            var restored = await service.RestoreBackup(fileName, current.Backups[0].FileName,
                new CharacterBackupRestore(current.Revision, current.Backups[0].Sha256), CancellationToken.None);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Equal(initial.Revision, restored.Revision);
            Assert.Contains(Directory.EnumerateFiles(Path.Combine(characters, "vsm-admin-backups"), "*.fch"),
                backup => File.ReadAllBytes(backup).SequenceEqual(changed));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] Profile(byte marker)
    {
        var payload = Enumerable.Repeat(marker, 96).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(payload.Length);
        writer.Write(payload);
        var signature = SHA512.HashData(payload);
        writer.Write(signature.Length);
        writer.Write(signature);
        writer.Flush();
        return stream.ToArray();
    }
}
