using UnityEngine;
using ValheimServerManager.Client;
using ValheimServerManager.VoiceSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

[Collection("Voice Unity fixture")]
public sealed class VoiceRelayTests
{
    [Theory]
    [InlineData(false, true, true, true, true, false, (int)VoiceRelayResult.Disabled)]
    [InlineData(true, false, true, true, true, false, (int)VoiceRelayResult.NotReady)]
    [InlineData(true, true, false, true, true, false, (int)VoiceRelayResult.NotParticipating)]
    [InlineData(true, true, true, false, true, false, (int)VoiceRelayResult.ConsentRequired)]
    [InlineData(true, true, true, true, false, false, (int)VoiceRelayResult.ModReceiptRequired)]
    [InlineData(true, true, true, true, true, true, (int)VoiceRelayResult.AdmissionRejected)]
    [InlineData(true, true, true, true, true, false, (int)VoiceRelayResult.Allowed)]
    public void RelayGatesExplainRejection(bool enabled, bool ready, bool participating, bool consent, bool receipt, bool kick, int reason)
        => Assert.Equal((VoiceRelayResult)reason, VoiceRelayRules.Eligibility(enabled, ready, participating, consent, receipt, kick));

    [Theory]
    [InlineData(true, true, 1, true, 0, (int)VoiceRelayResult.Self)]
    [InlineData(false, false, 1, true, 0, (int)VoiceRelayResult.CharacterMissing)]
    [InlineData(false, true, 1601, true, 0, (int)VoiceRelayResult.OutOfRange)]
    [InlineData(false, true, float.NaN, true, 0, (int)VoiceRelayResult.OutOfRange)]
    [InlineData(false, true, 1, false, 0, (int)VoiceRelayResult.Disconnected)]
    [InlineData(false, true, 1, true, 16384, (int)VoiceRelayResult.Backlog)]
    [InlineData(false, true, 1600, true, 16383, (int)VoiceRelayResult.Allowed)]
    public void RecipientsRespectRangeCharacterSocketAndBacklog(bool self, bool character, float distance, bool socket, int queued, int result)
        => Assert.Equal((VoiceRelayResult)result, VoiceRelayRules.Recipient(self, VoiceRelayResult.Allowed, character, distance, 40, socket, queued));

    [Fact]
    public void TwoSyntheticClientsCaptureRouteDecodeAndConsumeSamplesWithNoSelfLoopback()
    {
        VoiceAudioFixture.Reset();
        using var sender = new VoiceChatClient(); using var receiver = new VoiceChatClient();
        var relayed = 0;
        bool Route(string encoded)
        {
            Assert.Equal(VoiceRelayResult.Self, VoiceRelayRules.Recipient(true, VoiceRelayResult.Allowed, true, 0, 40, true, 0));
            if (VoiceRelayRules.Recipient(false, VoiceRelayRules.Eligibility(true, true, true, true, true, false), true, 4, 40, true, 0) != VoiceRelayResult.Allowed) return false;
            var packet = Convert.ToBase64String(VoiceCodec.Relay(42, 1, 2, 3, Convert.FromBase64String(encoded)));
            receiver.Receive(packet, 40, 1); relayed++; return true;
        }
        sender.Tick(true, VoiceChatMode.OpenMic, KeyCode.LeftAlt, "", 1, .015f, 1, Route);
        Time.unscaledTime += .12f; Microphone.Position = 1920;
        sender.Tick(true, VoiceChatMode.OpenMic, KeyCode.LeftAlt, "", 1, .015f, 1, Route);
        Assert.Equal(3, sender.SentFrames); Assert.Equal(3, relayed); Assert.Equal(0, sender.ReceivedFrames);
        Assert.Equal(3, receiver.DecodedFrames); Assert.Equal(1, receiver.PlaybackStarts);
        var output = new float[1280]; GameObject.Objects.Single().Source.clip.Callback(output);
        Assert.Contains(output, value => Math.Abs(value) > .1f); Assert.Equal(1280, receiver.OutputSamples);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(200)]
    public void ProductionBudgetAllowsNormalSpeechBatchedByRpcUpdate(int batchMs)
    {
        var budget=new VoiceFrameBudget(); var origin=new DateTime(2026,10,1,0,0,0,DateTimeKind.Utc);
        for(int generated=40;generated<=10000;generated+=40)
            Assert.True(budget.TryTake(origin.AddMilliseconds((generated+batchMs-1)/batchMs*batchMs)));
    }
    [Fact]
    public void ProductionBudgetBoundsAbuseAndDoesNotMintCreditsOnBackwardClock()
    {
        var budget=new VoiceFrameBudget(); var origin=new DateTime(2026,10,1,0,0,0,DateTimeKind.Utc);
        Assert.Equal(8,Enumerable.Range(0,1000).Count(_=>budget.TryTake(origin)));
        Assert.False(budget.TryTake(origin.AddSeconds(-1))); Assert.False(budget.TryTake(origin));
        Assert.True(budget.TryTake(origin.AddMilliseconds(40))); Assert.False(budget.TryTake(origin.AddMilliseconds(40)));
        Assert.Equal(25,Enumerable.Range(1,25).Count(i=>budget.TryTake(origin.AddMilliseconds(40+i*40))));
    }
    [Fact]
    public void AnonymousRelayCountersDistinguishRejectionFromNoRecipientAndRateLimitLogs()
    {
        var diagnostics = new VoiceRelayDiagnostics();
        diagnostics.Record(VoiceRelayResult.ConsentRequired); diagnostics.Record(VoiceRelayResult.NoRecipient);
        var first = diagnostics.Poll(10);
        Assert.Contains("ConsentRequired=1", first); Assert.Contains("NoRecipient=1", first);
        diagnostics.Record(VoiceRelayResult.Relayed); Assert.Null(diagnostics.Poll(11));
        Assert.Contains("Relayed=1", diagnostics.Poll(20)); Assert.Null(diagnostics.Poll(30));
    }
}
