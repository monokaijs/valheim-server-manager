using System;
using System.IO;
using ValheimServerManager.ServerSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class DeathFenceTests
{
    [Fact]
    public void PendingDeathSurvivesAReopenAndRejectsOtherTransactions()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-death-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var character = Path.Combine(root, "Steam_123_Player.fch");
            var first = Guid.NewGuid().ToString("N");
            var second = Guid.NewGuid().ToString("N");
            Assert.True(DeathFence.Begin(character, first));
            Assert.True(DeathFence.IsPending(character));
            Assert.True(DeathFence.Matches(character, first));
            Assert.True(DeathFence.Begin(character, first));
            Assert.False(DeathFence.Begin(character, second));
            Assert.Throws<InvalidDataException>(() => DeathFence.Complete(character, second));
            Assert.True(File.Exists(DeathFence.MarkerPath(character)));
            DeathFence.Complete(character, first);
            Assert.False(DeathFence.IsPending(character));
            Assert.True(DeathFence.IsCommitted(character, first));
            Assert.False(DeathFence.IsCommitted(character, second));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
