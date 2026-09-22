using ValheimServerManager.ClientSupport;
using ValheimServerManager.ServerSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class ClientExperienceTests
{
    [Fact]
    public void OptionalConnectionHooksAreDisabledForVanillaDefaults()
    {
        Assert.False(ConnectionPolicy.ShouldBufferWorldTraffic(false, false));
        Assert.True(ConnectionPolicy.ShouldBufferWorldTraffic(true, false));
        Assert.True(ConnectionPolicy.ShouldBufferWorldTraffic(false, true));
        Assert.False(ConnectionPolicy.ShouldOverridePlayerLimit(ConnectionPolicy.VanillaPlayerLimit));
        Assert.True(ConnectionPolicy.ShouldOverridePlayerLimit(20));
    }

    [Fact]
    public void VanillaConnectionDoesNotDelaySpawnAndRetriesAreBounded()
    {
        var handshake = new ClientHandshake();
        handshake.Begin(0);
        Assert.True(handshake.TrySendHello(0));
        Assert.False(handshake.TrySendHello(1));
        Assert.True(handshake.TrySendHello(2));
        Assert.True(handshake.TrySendHello(4));
        Assert.False(handshake.TrySendHello(6));
        Assert.False(handshake.ShouldWait);
        Assert.False(handshake.HasTimedOut(60));
    }

    [Fact]
    public void LocalCharacterPolicyStopsRetriesImmediately()
    {
        var handshake = new ClientHandshake();
        handshake.Begin(0);
        handshake.SetPolicy(false, 20, 0);
        Assert.True(handshake.Ready);
        Assert.False(handshake.ShouldWait);
        Assert.False(handshake.TrySendHello(2));
        Assert.False(handshake.TryReceiveProfile(3));
    }

    [Fact]
    public void RequiredCharacterFailsClosedAfterTimeoutAndDuplicatePolicyCannotExtendIt()
    {
        var handshake = new ClientHandshake();
        handshake.Begin(0);
        handshake.SetPolicy(true, 20, 0);
        handshake.SetPolicy(true, 20, 29);
        Assert.True(handshake.ShouldWait);
        Assert.False(handshake.HasTimedOut(29));
        Assert.True(handshake.HasTimedOut(30));
        handshake.Fail();
        handshake.CompleteProfile();
        Assert.True(handshake.ShouldWait);
        Assert.False(handshake.Ready);
        Assert.False(handshake.HasTimedOut(31));
        Assert.False(handshake.TryReceiveProfile(31));
    }

    [Fact]
    public void ProfileCanPrecedePolicyButCannotBeAppliedTwice()
    {
        var handshake = new ClientHandshake();
        handshake.Begin(0);
        Assert.True(handshake.TryReceiveProfile(1));
        handshake.SetPolicy(false, 20, 2);
        Assert.True(handshake.ShouldWait);
        Assert.False(handshake.TryReceiveProfile(2));
        handshake.CompleteProfile();
        Assert.False(handshake.ShouldWait);
        Assert.False(handshake.TrySendHello(3));
        handshake.Begin(5);
        Assert.False(handshake.RequiresCharacter);
        Assert.True(handshake.TryReceiveProfile(6));
    }

    [Fact]
    public void WelcomeLifetimeStartsAfterWorldLoading()
    {
        var notices = new NoticeQueue();
        notices.Add(new ClientNotice("Welcome", "Welcome to the server.", NoticeKind.Information), 0);
        notices.Tick(false, 120);
        Assert.Null(notices.Current);
        notices.Tick(true, 120);
        Assert.NotNull(notices.Current);
        Assert.Equal("Welcome", notices.Current.Title);
        notices.Tick(true, 127);
        Assert.NotNull(notices.Current);
        notices.Tick(true, 129);
        Assert.Null(notices.Current);
    }

    [Fact]
    public void DisconnectNoticePreemptsWelcomeAndSurvivesSceneResetUntilDismissed()
    {
        var notices = new NoticeQueue();
        notices.Add(new ClientNotice("Welcome", "Hello", NoticeKind.Information), 0);
        notices.Tick(true, 0);
        notices.Add(new ClientNotice("Kicked", "Please contact an administrator.", NoticeKind.Attention), 1);
        notices.Tick(true, 1);
        Assert.Equal("Kicked", notices.Current.Title);
        notices.ClearTransient();
        notices.Tick(false, 600);
        Assert.Equal("Kicked", notices.Current.Title);
        notices.Dismiss();
        notices.Tick(false, 601);
        Assert.Null(notices.Current);
    }

    [Fact]
    public void DuplicateNoticesAreSuppressedAndQueueIsBounded()
    {
        var notices = new NoticeQueue();
        var notice = new ClientNotice("Kicked", "Reason", NoticeKind.Attention);
        notices.Add(notice, 0);
        notices.Add(notice, 1);
        Assert.Equal(1, notices.Count);
        notices.Tick(false, 1);
        notices.Dismiss();
        notices.Add(notice, 2);
        Assert.Equal(0, notices.Count);
        for (var i = 0; i < 50; i++)
            notices.Add(new ClientNotice("Notice " + i, "Reason", NoticeKind.Attention), 3);
        Assert.Equal(8, notices.Count);
    }

    [Fact]
    public void NoticeTextIsBoundedAndControlCharactersAreRemoved()
    {
        var notice = new ClientNotice(new string('x', 200), "  hello\0\t\nworld  ", NoticeKind.Error);
        Assert.Equal(80, notice.Title.Length);
        Assert.Equal("hello\nworld", notice.Message);
        Assert.Equal(1600, new ClientNotice("Error", new string('x', 4000), NoticeKind.Error).Message.Length);
        Assert.Equal("x", ClientNotice.Clean("x😀", 2, ""));
    }
}
