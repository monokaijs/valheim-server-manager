using System;
using System.IO;

namespace ValheimServerManager.VoiceSupport;

// G.711 mu-law keeps a 40 ms mono frame small enough for the existing game RPC.
internal static class VoiceCodec
{
    internal const int SampleRate = 16000;
    internal const int FrameSamples = 640;
    internal const int RelayBytes = 1 + 8 + 12 + FrameSamples;

    internal static byte[] Encode(float[] samples)
    {
        if (samples == null || samples.Length != FrameSamples) throw new ArgumentException("Invalid voice frame.", nameof(samples));
        var encoded = new byte[FrameSamples];
        for (var index = 0; index < encoded.Length; index++)
        {
            var sample = Math.Max(-1f, Math.Min(1f, samples[index]));
            var pcm = (int)(sample * 32767f);
            var sign = pcm < 0 ? 0x80 : 0;
            if (pcm < 0) pcm = -pcm;
            pcm = Math.Min(pcm, 32635) + 132;
            var exponent = 7;
            for (var mask = 0x4000; exponent > 0 && (pcm & mask) == 0; exponent--, mask >>= 1) { }
            var mantissa = (pcm >> (exponent + 3)) & 0x0f;
            encoded[index] = (byte)~(sign | exponent << 4 | mantissa);
        }
        return encoded;
    }

    internal static float Decode(byte value)
    {
        var code = (~value) & 0xff;
        var sample = ((code & 0x0f) << 3) + 132;
        sample <<= (code >> 4) & 7;
        sample -= 132;
        if ((code & 0x80) != 0) sample = -sample;
        return Math.Max(-1f, Math.Min(1f, sample / 32768f));
    }

    internal static byte[] Relay(long sender, float x, float y, float z, byte[] frame)
    {
        if (frame == null || frame.Length != FrameSamples) throw new ArgumentException("Invalid voice frame.", nameof(frame));
        using (var stream = new MemoryStream(RelayBytes))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)1);
            writer.Write(sender);
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
            writer.Write(frame);
            return stream.ToArray();
        }
    }

    internal static bool TryReadRelay(byte[] payload, out long sender, out float x, out float y, out float z, out byte[] frame)
    {
        sender = 0; x = y = z = 0; frame = Array.Empty<byte>();
        if (payload == null || payload.Length != RelayBytes || payload[0] != 1) return false;
        using (var stream = new MemoryStream(payload, false))
        using (var reader = new BinaryReader(stream))
        {
            reader.ReadByte();
            sender = reader.ReadInt64();
            x = reader.ReadSingle(); y = reader.ReadSingle(); z = reader.ReadSingle();
            if (sender == 0 || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
            frame = reader.ReadBytes(FrameSamples);
            return frame.Length == FrameSamples;
        }
    }
}
