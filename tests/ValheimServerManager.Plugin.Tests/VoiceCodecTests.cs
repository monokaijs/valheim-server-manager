using ValheimServerManager.VoiceSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class VoiceCodecTests
{
    [Fact]
    public void SpeechFrameRoundTripsWithinTelephoneCodecPrecision()
    {
        var samples = Enumerable.Range(0, VoiceCodec.FrameSamples)
            .Select(index => (float)(0.5 * Math.Sin(index * 2 * Math.PI * 440 / VoiceCodec.SampleRate))).ToArray();
        var encoded = VoiceCodec.Encode(samples);
        Assert.Equal(VoiceCodec.FrameSamples, encoded.Length);
        var error = samples.Select((sample, index) => Math.Abs(sample - VoiceCodec.Decode(encoded[index]))).Average();
        Assert.True(error < 0.02, $"Mean codec error was {error}");
    }

    [Fact]
    public void RelayEnvelopeRejectsMalformedFrames()
    {
        var encoded = VoiceCodec.Encode(new float[VoiceCodec.FrameSamples]);
        var packet = VoiceCodec.Relay(42, 1, 2, 3, encoded);
        Assert.True(VoiceCodec.TryReadRelay(packet, out var sender, out var x, out var y, out var z, out var received));
        Assert.Equal(42, sender);
        Assert.Equal((1f, 2f, 3f), (x, y, z));
        Assert.Equal(encoded, received);
        packet[0] = 2;
        Assert.False(VoiceCodec.TryReadRelay(packet, out _, out _, out _, out _, out _));
    }
}
