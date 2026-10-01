using ValheimServerManager.ClientSupport;
using ValheimServerManager.Server;
using ValheimServerManager.ServerSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class ServerAdmissionTests
{
    [Fact]
    public void EarlyHelloCompletesAfterAuthenticatedIdBeforeAdmissionIsTested()
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(0);
        var client = new ClientHandshake();
        client.Begin(0);
        Assert.True(client.TrySendHello(0));
        server.Hello(peer, true, "2.7.3");
        server.Authenticate(peer, -381381653);
        peer.OnPolicy = () => client.SetPolicy(true, 20, 1);

        Assert.False(server.Admit(peer));
        Assert.True(server.CharacterCapable(peer));
        Assert.True(server.JoinedPeer(peer));
        Assert.Equal(1, server.ProfileSends);
        Assert.True(client.Acknowledged);
        Assert.False(client.TrySendHello(2));
        server.Enforce();
        Assert.False(server.HasKick(peer));
        Assert.False(server.Admit(peer));
        Assert.Equal(1, server.ProfileSends);
    }

    [Fact]
    public void UnauthenticatedPeerCannotConsumeHelloOrLoadProfile()
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(0);
        server.Hello(peer, true, "2.7.3");
        Assert.True(server.Admit(peer));
        Assert.False(server.CharacterCapable(peer));
        Assert.Equal(0, server.ProfileSends);
        server.Authenticate(peer, 42);
        Assert.False(server.Admit(peer));
        Assert.True(server.CharacterCapable(peer));
    }

    [Theory]
    [InlineData("2.7.1")]
    [InlineData("invalid")]
    public void OldOrInvalidClientStillCannotPassCharacterAdmission(string version)
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(0);
        server.Hello(peer, true, version);
        server.Authenticate(peer, 42);
        Assert.True(server.Admit(peer));
        Assert.False(server.CharacterCapable(peer));
        server.Enforce();
        Assert.True(server.HasKick(peer));
    }

    [Fact]
    public void CharacterCapabilityDoesNotBypassMissingModReceipt()
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(0);
        server.Hello(peer, true, "2.7.3");
        server.Authenticate(peer, 42, receipt: false);
        Assert.True(server.Admit(peer));
        Assert.True(server.CharacterCapable(peer));
        Assert.False(server.JoinedPeer(peer));
        server.SetReceipt(peer);
        Assert.False(server.Admit(peer));
        Assert.Equal(1, server.ProfileSends);
    }

    [Fact]
    public void ValidLateHelloCancelsCharacterKickWithoutEnablingInspection()
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(42);
        server.Enforce();
        Assert.True(server.HasKick(peer));
        server.Hello(peer, false, "2.7.3");
        Assert.False(server.HasKick(peer));
        Assert.True(server.CharacterCapable(peer));
        Assert.False(server.InventoryAllowed(peer));
        server.Hello(peer, false, "2.7.3");
        server.Enforce();
        Assert.False(server.HasKick(peer));
        Assert.Equal(1, server.ProfileSends);
    }

    [Fact]
    public void UnrelatedKickSurvivesEnforcementAndValidHello()
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(42);
        var unrelated = new ScheduledKick { Due = DateTime.UtcNow.AddSeconds(3) };
        server.SetKick(peer, unrelated);
        server.Enforce();
        Assert.Same(unrelated, server.Kick(peer));
        server.Hello(peer, true, "2.7.3");
        Assert.Same(unrelated, server.Kick(peer));
    }

    [Fact]
    public void UnsupportedLateHelloCannotCancelCharacterKick()
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(42);
        server.Enforce();
        var kick = server.Kick(peer);
        server.Hello(peer, true, "2.7.1");
        Assert.Same(kick, server.Kick(peer));
        Assert.True(kick.ServerCharacterRequirement);
        Assert.True(server.Admit(peer));
    }

    [Fact]
    public void RepeatedEarlyHellosRetainLatestPrivacyChoice()
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(0);
        server.Hello(peer, true, "2.7.3");
        server.Hello(peer, false, "2.7.3");
        server.Authenticate(peer, 42);
        Assert.False(server.Admit(peer));
        Assert.False(server.InventoryAllowed(peer));
        Assert.Equal(1, server.ProfileSends);
    }

    [Fact]
    public void CharacterHelloPreservesUnsatisfiedModAndInspectionRequirements()
    {
        var server = new ServerPlugin();
        var peer = server.AddPeer(42);
        var kick = new ScheduledKick { ServerCharacterRequirement = true, InspectionRequirement = true, ModRequirement = true };
        server.SetKick(peer, kick);
        server.Hello(peer, false, "2.7.3");
        Assert.Same(kick, server.Kick(peer));
        Assert.False(kick.ServerCharacterRequirement);
        Assert.True(kick.InspectionRequirement);
        Assert.True(kick.ModRequirement);
        server.Hello(peer, true, "2.7.3");
        Assert.Same(kick, server.Kick(peer));
        Assert.False(kick.InspectionRequirement);
        Assert.True(kick.ModRequirement);
        server.SatisfyMods(peer);
        Assert.False(server.HasKick(peer));
    }

    [Fact]
    public void CompletionRemovesPendingHelloBeforeReentrantJoin()
    {
        var pending = new PendingClientHellos<object>();
        var peer = new object();
        Assert.True(pending.DeferIfUnauthenticated(peer, 0, true, "2.7.3"));
        var calls = 0;
        pending.Complete(peer, 42, (_, _) =>
        {
            calls++;
            pending.Complete(peer, 42, (_, _) => calls++);
        });
        pending.Complete(peer, 42, (_, _) => calls++);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DisconnectedHelloCannotLeakIntoReconnectedPeerWithSameId()
    {
        var server = new ServerPlugin();
        var oldPeer = server.AddPeer(0);
        server.Hello(oldPeer, true, "2.7.3");
        server.ForgetPending(oldPeer);
        var newPeer = server.AddPeer(42);
        Assert.True(server.Admit(newPeer));
        Assert.False(server.CharacterCapable(newPeer));
        server.Authenticate(oldPeer, 99);
        Assert.True(server.Admit(oldPeer));
        server.Hello(newPeer, true, "2.7.3");
        Assert.False(server.Admit(newPeer));
    }
}
