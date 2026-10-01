using UnityEngine;
using ValheimServerManager.Client;
using ValheimServerManager.VoiceSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

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
        Time.unscaledTime += .08f; Microphone.Position = 1280;
        sender.Tick(true, VoiceChatMode.OpenMic, KeyCode.LeftAlt, "", 1, .015f, 1, Route);
        Assert.Equal(2, sender.SentFrames); Assert.Equal(2, relayed); Assert.Equal(0, sender.ReceivedFrames);
        Assert.Equal(2, receiver.DecodedFrames); Assert.Equal(1, receiver.PlaybackStarts);
        var output = new float[1280]; GameObject.Objects.Single().Source.clip.Callback(output);
        Assert.Contains(output, value => Math.Abs(value) > .1f); Assert.Equal(1280, receiver.OutputSamples);
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
