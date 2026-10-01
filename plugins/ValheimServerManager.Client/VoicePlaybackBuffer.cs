#nullable disable
using System;
using System.Collections.Generic;
using ValheimServerManager.VoiceSupport;

namespace ValheimServerManager.Client;

// Accessed only while Playback holds its audio gate. Silence during priming is
// intentional; a drained stream accumulates a fresh cushion instead of exposing
// each late frame immediately. The native callback size sets a bounded target.
internal sealed class VoicePlaybackBuffer
{
    internal const int InitialSamples = VoiceCodec.FrameSamples * 3; // 120 ms
    internal const int MaximumSamples = VoiceCodec.FrameSamples * 12; // 480 ms
    private readonly Queue<float> _samples = new();
    private int _target = InitialSamples;
    private bool _primed;
    internal int Count => _samples.Count;
    internal int Target => _target;
    internal void Clear() { _samples.Clear(); _target = InitialSamples; _primed = false; }
    internal int Add(byte[] frame)
    {
        for (var index = 0; index < frame.Length; index++) _samples.Enqueue(VoiceCodec.Decode(frame[index]));
        var excess = _samples.Count - MaximumSamples;
        var dropped = excess <= 0 ? 0 : (excess + VoiceCodec.FrameSamples - 1) / VoiceCodec.FrameSamples * VoiceCodec.FrameSamples;
        for (var index = 0; index < dropped; index++) _samples.Dequeue();
        return dropped;
    }
    internal int Read(float[] data, out bool underrun, out bool waiting)
    {
        underrun = waiting = false;
        if (!_primed)
        {
            _target = Math.Max(_target, Math.Min(MaximumSamples, data.Length + VoiceCodec.FrameSamples));
            if (_samples.Count < _target) { Array.Clear(data, 0, data.Length); waiting = true; return 0; }
            _primed = true;
        }
        var consumed = Math.Min(_samples.Count, data.Length);
        for (var index = 0; index < consumed; index++) data[index] = _samples.Dequeue();
        if (consumed < data.Length)
        {
            Array.Clear(data, consumed, data.Length - consumed);
            // Avoid a sharp cutoff into silence, without repeating old speech.
            var fade = Math.Min(64, consumed);
            for (var index = 0; index < fade; index++) data[consumed - fade + index] *= (fade - 1 - index) / (float)fade;
            underrun = true; _primed = false;
            _target = Math.Min(MaximumSamples, Math.Max(_target + VoiceCodec.FrameSamples, data.Length + VoiceCodec.FrameSamples));
        }
        return consumed;
    }
}
