using System;
using System.IO;
using ValheimServerManager.ServerSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class PeriodicCharacterBackupTests
{
    [Fact]
    public void SavesAtThirtyMinuteIntervalsAndRetainsFivePerCharacter()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-periodic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var character = Path.Combine(root, "Steam_123_Hero.fch");
            var other = Path.Combine(root, "Steam_456_Hero.fch");
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(PeriodicCharacterBackup.SaveIfDue(character, new byte[] { 1 }, start));
            Assert.False(PeriodicCharacterBackup.SaveIfDue(character, new byte[] { 2 }, start.AddMinutes(29)));
            Assert.Single(PeriodicCharacterBackup.Files(character));
            Assert.True(PeriodicCharacterBackup.SaveIfDue(other, new byte[] { 7 }, start));
            for (var index = 1; index <= 5; index++)
                Assert.True(PeriodicCharacterBackup.SaveIfDue(character, new[] { (byte)(index + 1) }, start.AddMinutes(30 * index)));
            var backups = PeriodicCharacterBackup.Files(character);
            Assert.Equal(5, backups.Length);
            Assert.Equal(new byte[] { 6 }, File.ReadAllBytes(backups[0]));
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(backups[4]));
            Assert.Single(PeriodicCharacterBackup.Files(other));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
