using UnityEngine;
using ValheimServerManager.Client;
using ValheimServerManager.VoiceSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class VoiceChatClientTests : IDisposable
{
    private readonly VoiceChatClient _client = new();
    private readonly List<string> _sent = new();
    public VoiceChatClientTests() => VoiceAudioFixture.Reset();
    public void Dispose() => _client.Dispose();
    private void Tick(float time, int position, VoiceChatMode mode = VoiceChatMode.OpenMic,
        bool active = true, string device = "", float gain = 1, float threshold = .015f)
    {
        Time.unscaledTime = time; Microphone.Position = position;
        _client.Tick(active, mode, KeyCode.LeftAlt, device, gain, threshold, 1, packet => { _sent.Add(packet); return true; });
    }
    private static float[] Decode(IEnumerable<string> packets) => packets.SelectMany(packet => Convert.FromBase64String(packet).Select(VoiceCodec.Decode)).ToArray();
    private static string Relay(long sender = 42) => Convert.ToBase64String(VoiceCodec.Relay(sender, 1, 2, 3, VoiceCodec.Encode(new float[640])));
    private static void AssertAudio(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.Zip(actual, (a, b) => Math.Abs(a - b)).Average() < .02);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ChannelFramesDownmixAndRoundTripThroughPlayback(int channels)
    {
        var clip = VoiceAudioFixture.Signal(channels);
        Microphone.StartClip = _ => clip;
        Tick(10, 0); Tick(10.08f, 1280);
        Assert.Equal(2, _sent.Count);
        Assert.Equal([(0, 640 * channels), (640, 640 * channels)], clip.Reads);
        AssertAudio(Enumerable.Range(0, 1280).Select(i => VoiceAudioFixture.MonoSample(clip, i)).ToArray(), Decode(_sent));
        using var receiver = new VoiceChatClient();
        foreach (var packet in _sent)
            receiver.Receive(Convert.ToBase64String(VoiceCodec.Relay(42, 1, 2, 3, Convert.FromBase64String(packet))), 40, 1);
        var source = GameObject.Objects.Single().Source;
        Assert.True(source.isPlaying);
        var output = new float[1280]; source.clip.Callback(output);
        AssertAudio(Decode(_sent), output);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CaptureWrapsUsingActualSampleFrameCapacity(int channels)
    {
        var clip = VoiceAudioFixture.Signal(channels, 3000);
        Microphone.StartClip = _ => clip;
        Tick(10, 0); Tick(10.08f, 1280); Tick(10.16f, 2560);
        _sent.Clear(); clip.Reads.Clear();
        Tick(10.24f, 840);
        Assert.Equal([(2560, 640 * channels), (200, 640 * channels)], clip.Reads);
        AssertAudio(Enumerable.Range(2560, 1280).Select(i => VoiceAudioFixture.MonoSample(clip, i)).ToArray(), Decode(_sent));
    }

    [Fact]
    public void LargeCaptureBacklogResumesAtLatestCompleteFrame()
    {
        var clip = VoiceAudioFixture.Signal(2);
        Microphone.StartClip = _ => clip;
        Tick(10, 0); Tick(10.7f, 10000);
        Assert.Single(_sent);
        Assert.Equal([(9360, 1280)], clip.Reads);
        AssertAudio(Enumerable.Range(9360, 640).Select(i => VoiceAudioFixture.MonoSample(clip, i)).ToArray(), Decode(_sent));
    }

    [Fact]
    public void TransientReadFailureReportsUnavailableAndRecoversWithoutLosingCursor()
    {
        Tick(10, 0);
        var clip = Microphone.LastStarted!; clip.Readable = false;
        Tick(10.04f, 640);
        Assert.True(_client.MicrophoneUnavailable); Assert.Empty(_sent);
        clip.Readable = true; Tick(10.08f, 1280);
        Assert.False(_client.MicrophoneUnavailable);
        Assert.Equal(1, Microphone.Starts); Assert.Equal(0, Microphone.Ends);
        Assert.Equal([0, 0, 640], clip.Reads.Select(read => read.Offset));
        AssertAudio(Enumerable.Range(0, 1280).Select(i => VoiceAudioFixture.MonoSample(clip, i)).ToArray(), Decode(_sent));
    }

    [Fact]
    public void PersistentReadFailureCleansUpAndRetriesOnlyAfterBackoff()
    {
        Tick(10, 0); var failed = Microphone.LastStarted!; failed.Readable = false;
        Tick(10.04f, 640); Tick(10.55f, 1280);
        Assert.True(failed.Destroyed); Assert.Equal(1, Microphone.Ends);
        Assert.True(_client.MicrophoneUnavailable); Assert.False(_client.Transmitting);
        Tick(13.54f, 0); Assert.Equal(1, Microphone.Starts);
        Tick(13.56f, 0); Assert.Equal(2, Microphone.Starts);
        Assert.True(_client.MicrophoneUnavailable);
        Tick(13.6f, 640); Assert.False(_client.MicrophoneUnavailable);
        Assert.Single(_sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StalledStartupOrPreviouslyProgressingCaptureHasBoundedTimeout(bool progressed)
    {
        Tick(10, 0); var failed = Microphone.LastStarted!;
        var position = progressed ? 640 : 0;
        if (progressed) Tick(10.04f, position);
        Tick(12.05f, position);
        Assert.True(_client.MicrophoneUnavailable); Assert.True(failed.Destroyed);
        Assert.Equal(1, Microphone.Ends);
        Tick(15.04f, 0); Assert.Equal(1, Microphone.Starts);
        Tick(15.06f, 0); Assert.Equal(2, Microphone.Starts);
    }

    [Fact]
    public void InvalidPositionCanRecoverTransientlyButCannotRemainStuck()
    {
        Tick(10, 0); Tick(10.04f, -1);
        Assert.True(_client.MicrophoneUnavailable); Assert.Equal(0, Microphone.Ends);
        Tick(10.08f, 640); Assert.False(_client.MicrophoneUnavailable);
        Tick(12.09f, 16000);
        Assert.True(_client.MicrophoneUnavailable); Assert.Equal(1, Microphone.Ends);
    }

    [Theory]
    [InlineData(0, 16000, 16000)]
    [InlineData(33, 16000, 16000)]
    [InlineData(1, 640, 16000)]
    [InlineData(1, 16000, 48000)]
    public void InvalidCaptureFormatIsStoppedBeforeReading(int channels, int capacity, int frequency)
    {
        var clip = new AudioClip { channels = channels, samples = capacity, frequency = frequency };
        Microphone.StartClip = _ => clip;
        Tick(10, 0);
        Assert.True(_client.MicrophoneUnavailable); Assert.True(clip.Destroyed);
        Assert.Empty(clip.Reads); Assert.Equal(1, Microphone.Ends);
    }

    [Fact]
    public void MissingDeviceStartupFailureAndCaptureExceptionCleanUpSafely()
    {
        Tick(10, 0, device: "missing");
        Assert.True(_client.MicrophoneUnavailable); Assert.Equal(0, Microphone.Starts);
        Microphone.StartThrows = true;
        Tick(10.1f, 0, device: "first");
        Assert.True(_client.MicrophoneUnavailable); Assert.Equal(1, Microphone.Ends);
        Microphone.StartThrows = false; Tick(13.11f, 0, device: "first");
        Microphone.PositionThrows = true; Tick(13.15f, 640, device: "first");
        Assert.True(_client.MicrophoneUnavailable); Assert.Equal(2, Microphone.Ends);
        Assert.True(Microphone.LastStarted!.Destroyed);
    }

    [Fact]
    public void NullStartupResultEndsAttemptAndRetainsFailureUntilSuccessfulRead()
    {
        Microphone.StartClip = _ => null;
        Tick(10, 0);
        Assert.True(_client.MicrophoneUnavailable); Assert.Equal(1, Microphone.Ends);
        Microphone.StartClip = _ => VoiceAudioFixture.Signal();
        Tick(13.01f, 0); Assert.True(_client.MicrophoneUnavailable);
        Tick(13.05f, 640); Assert.False(_client.MicrophoneUnavailable);
    }

    [Fact]
    public void DeviceChangeAndResetDiscardCapturePlaybackAndFailureState()
    {
        Tick(10, 0, device: "first"); var first = Microphone.LastStarted!;
        first.Readable = false; Tick(10.04f, 640, device: "first");
        Tick(10.05f, 0, device: "second");
        Assert.True(first.Destroyed); Assert.Equal(["first"], Microphone.EndDevices);
        Assert.False(_client.MicrophoneUnavailable);
        Assert.Equal(["first", "second"], Microphone.StartDevices);
        _client.Receive(Relay(), 40, 1); _client.Receive(Relay(), 40, 1);
        var playback = GameObject.Objects.Single();
        _client.Reset(); _client.Reset();
        Assert.Equal(2, Microphone.Ends); Assert.True(Microphone.LastStarted!.Destroyed);
        Assert.True(playback.Destroyed); Assert.True(playback.Source.clip.Destroyed);
        Assert.False(playback.Source.isPlaying); Assert.False(_client.MicrophoneUnavailable);
        Assert.False(_client.Transmitting);
        Tick(10.06f, 0, device: "second"); Assert.Equal(3, Microphone.Starts);
    }

    [Fact]
    public void ResetDuringBackoffAllowsCleanImmediateRestart()
    {
        Tick(10, 0); Tick(12.01f, 0);
        Assert.True(_client.MicrophoneUnavailable);
        _client.Reset();
        Assert.False(_client.MicrophoneUnavailable);
        Tick(12.02f, 0); Tick(12.06f, 640);
        Assert.Equal(2, Microphone.Starts); Assert.Equal(1, Microphone.Ends);
        Assert.False(_client.MicrophoneUnavailable); Assert.Single(_sent);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NonFiniteChannelDataAndGainDoNotPoisonEncodedFrame(float gain)
    {
        var clip = VoiceAudioFixture.Signal(2);
        for (var index = 1; index < clip.Data.Length; index += 2) clip.Data[index] = float.NaN;
        Microphone.StartClip = _ => clip;
        Tick(10, 0, gain: gain); Tick(10.04f, 640, gain: gain);
        Assert.Single(_sent);
        AssertAudio(Enumerable.Range(0, 640).Select(i => VoiceAudioFixture.ChannelSample(i, 0) / 2).ToArray(), Decode(_sent));
        Assert.False(_client.MicrophoneUnavailable);
    }

    [Fact]
    public void InactiveAndIdlePushToTalkNeverOpenDeviceAndReleaseStopsOnce()
    {
        Tick(10, 0, active: false); Tick(10, 0, VoiceChatMode.PushToTalk);
        Assert.Equal(0, Microphone.Starts);
        Input.Pressed = true; Tick(10, 0, VoiceChatMode.PushToTalk);
        Assert.False(_client.Transmitting);
        Tick(10.04f, 640, VoiceChatMode.PushToTalk); Assert.True(_client.Transmitting);
        Input.Pressed = false; Tick(10.05f, 640, VoiceChatMode.PushToTalk);
        Tick(10.06f, 640, VoiceChatMode.PushToTalk);
        Assert.Equal(1, Microphone.Ends); Assert.False(_client.Transmitting);
    }

    [Fact]
    public void SilentActivationStillProvesCaptureHealthAndSpeechOpensGate()
    {
        var clip = VoiceAudioFixture.Signal(silence: true);
        Microphone.StartClip = _ => clip;
        Tick(10, 0, VoiceChatMode.VoiceActivation); clip.Readable = false;
        Tick(10.04f, 640, VoiceChatMode.VoiceActivation);
        clip.Readable = true; Tick(10.08f, 1280, VoiceChatMode.VoiceActivation);
        Assert.False(_client.MicrophoneUnavailable); Assert.Empty(_sent);
        clip.Data = VoiceAudioFixture.Signal().Data;
        Tick(10.12f, 1920, VoiceChatMode.VoiceActivation); Assert.Single(_sent);
        Array.Clear(clip.Data); Tick(10.16f, 2560, VoiceChatMode.VoiceActivation);
        Assert.Equal(2, _sent.Count);
        Tick(10.5f, 3200, VoiceChatMode.VoiceActivation); Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void RangeChangesUpdateExistingSpeakersWithoutAnotherFrameAndRejectNonFiniteValues()
    {
        _client.Receive(Relay(42), 40, 1); _client.Receive(Relay(99), 40, 1);
        var sources = GameObject.Objects.Select(obj => obj.Source).ToArray();
        foreach (var range in new[] { 100f, 5f, 25f, -10f, 1000f })
        {
            _client.SetPlaybackRange(range);
            Assert.All(sources, source => Assert.Equal(Math.Clamp(range, 5, 100), source.maxDistance));
        }
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            _client.SetPlaybackRange(invalid); _client.Receive(Relay(42), invalid, invalid);
            Assert.All(sources, source => Assert.Equal(100, source.maxDistance));
        }
        _client.Receive(Relay(42), 12, .5f);
        Assert.Equal(12, sources[0].maxDistance); Assert.Equal(.5f, sources[0].volume);
        Assert.Equal(2, GameObject.Objects.Count);
    }

    [Fact]
    public void InvalidInitialPlaybackPolicyUsesFiniteDefaultsAndInactiveTickDisposesStreams()
    {
        _client.Receive(Relay(), float.NaN, float.PositiveInfinity);
        var obj = GameObject.Objects.Single();
        Assert.Equal(40, obj.Source.maxDistance); Assert.Equal(1, obj.Source.volume);
        Tick(10, 0, active: false);
        Assert.True(obj.Destroyed); Assert.True(obj.Source.clip.Destroyed);
        Assert.Equal(0, Microphone.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DroppedOrThrowingTransportDoesNotClaimTransmissionOrFailCapture(bool throws)
    {
        Tick(10, 0);
        Time.unscaledTime = 10.04f; Microphone.Position = 640;
        _client.Tick(true, VoiceChatMode.OpenMic, KeyCode.LeftAlt, "", 1, .015f, 1,
            _ => throws ? throw new IOException("synthetic private payload") : false);
        Assert.Equal(1, _client.CapturedFrames); Assert.Equal(1, _client.EncodedFrames);
        Assert.Equal(0, _client.SentFrames); Assert.Equal(1, _client.SendDrops);
        Assert.False(_client.Transmitting); Assert.False(_client.Speaking);
        Assert.False(_client.MicrophoneUnavailable); Assert.Equal(0, Microphone.Ends);
        Assert.DoesNotContain("private", _client.TransportStatus);
        Tick(10.08f, 1280); Assert.True(_client.Speaking); Assert.Equal(1, _client.SentFrames);
    }

    [Theory]
    [InlineData((int)VoiceChatMode.OpenMic)]
    [InlineData((int)VoiceChatMode.PushToTalk)]
    public void SilenceCanBeSentButNeverDisplaysSpeakingHud(int value)
    {
        var mode = (VoiceChatMode)value;
        Input.Pressed = true; Microphone.StartClip = _ => VoiceAudioFixture.Signal(silence: true);
        Tick(10, 0, mode); Tick(10.04f, 640, mode);
        Assert.True(_client.Transmitting); Assert.False(_client.Speaking); Assert.Equal(0, _client.WaveCount);
        Assert.Equal(1, _client.CapturedFrames); Assert.Contains("silent", _client.CaptureStatus);
        Microphone.LastStarted!.Data = VoiceAudioFixture.Signal().Data;
        Tick(10.08f, 1280, mode); Assert.True(_client.Speaking); Assert.Equal(3, _client.WaveCount);
        Tick(10.09f, 1280, mode, active: false); Assert.False(_client.Speaking);
    }

    [Fact]
    public void ClippingAndCaptureFailuresHaveActionableDiagnosticsWithoutDeviceNames()
    {
        Tick(10, 0, device: "first", gain: 3); Tick(10.04f, 640, device: "first", gain: 3);
        Assert.Equal(1, _client.ClippedFrames); Assert.Contains("reduce", _client.CaptureStatus);
        Assert.DoesNotContain("first", _client.CaptureStatus);
        Tick(12.1f, 640, device: "first"); Assert.Equal(1, _client.CaptureFailures);
        Assert.Contains("stalled", _client.CaptureStatus); Assert.False(_client.Speaking);
    }

    [Fact]
    public void DiagnosticsAreRateLimitedAndCountersResetPerConnection()
    {
        var log = new List<string>(); _client.Diagnose(log.Add, "ready");
        Tick(10, 0); Tick(10.04f, 640); _client.Diagnose(log.Add, "ready"); Assert.Single(log);
        Time.unscaledTime = 20; _client.Diagnose(log.Add, "ready"); Assert.Equal(2, log.Count);
        Assert.Contains("sent=1", log[1]); Assert.DoesNotContain(_sent[0], log[1]);
        _client.Reset(); Assert.Equal(0, _client.CapturedFrames); Assert.Equal(0, _client.SentFrames);
        Assert.Equal(0, _client.OutputSamples); Assert.False(_client.Speaking);
    }

    [Fact]
    public void ReceiveTracksInvalidDecodeOutputAndUnderrunSeparately()
    {
        _client.Receive("not base64", 40, 1); Assert.Equal(1, _client.InvalidPackets);
        _client.Receive(new string('x', 901), 40, 1); Assert.Equal(2, _client.InvalidPackets);
        _client.Receive(Relay(), 40, 1); _client.Receive(Relay(), 40, 1);
        var source = GameObject.Objects.Single().Source;
        Assert.Equal(4, _client.ReceivedFrames); Assert.Equal(2, _client.DecodedFrames);
        Assert.Equal(1, _client.PlaybackStarts); Assert.Equal(0, _client.OutputSamples);
        source.clip.Callback(new float[1400]);
        Assert.Equal(1280, _client.OutputSamples); Assert.Equal(1, _client.Underruns);
    }

    [Fact]
    public void PlaybackExceptionCleansUpAndNextFrameCanRecover()
    {
        _client.Receive(Relay(), 40, 1);
        AudioSource.PlayThrows = true; _client.Receive(Relay(), 40, 1);
        Assert.Equal(1, _client.PlaybackFailures); Assert.Contains("check output", _client.PlaybackStatus);
        Assert.True(GameObject.Objects[0].Destroyed); Assert.True(GameObject.Objects[0].Source.clip.Destroyed);
        AudioSource.PlayThrows = false; _client.Receive(Relay(), 40, 1); _client.Receive(Relay(), 40, 1);
        Assert.True(GameObject.Objects[1].Source.isPlaying);
    }

    [Fact]
    public void PlaybackCreationFailureAndReceiveGateHaveSeparateCountersAndCleanResources()
    {
        _client.RejectReceive(); Assert.Equal(1, _client.ReceiveDrops); Assert.Equal(0, _client.ReceivedFrames);
        AudioClip.CreateFails = true; _client.Receive(Relay(), 40, 1);
        Assert.Equal(1, _client.PlaybackFailures); Assert.Equal(0, _client.DecodedFrames);
        Assert.True(GameObject.Objects.Single().Destroyed); Assert.Contains("check output", _client.PlaybackStatus);
        AudioClip.CreateFails = false; _client.Receive(Relay(), 40, 1); _client.Receive(Relay(), 40, 1);
        Assert.Equal(2, _client.DecodedFrames); Assert.True(GameObject.Objects.Last().Source.isPlaying);
    }
}
