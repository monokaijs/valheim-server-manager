using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimServerManager.VoiceSupport;

namespace ValheimServerManager.Client;

internal enum VoiceChatMode { PushToTalk, VoiceActivation, OpenMic }

internal sealed class VoiceChatClient : IDisposable
{
    // Unity 6 also exposes a Span overload that cannot be resolved by this net48 plugin.
    private static readonly Func<AudioClip, float[], int, bool> ReadMicrophone = CreateReader();
    private AudioClip _microphoneClip;
    private string _requestedDevice;
    private string _activeDevice;
    private int _readPosition;
    private float _retryAt;
    private float _voiceGateUntil;
    private float _lastSentAt;
    private readonly Dictionary<long, Playback> _playbacks = new();
    internal bool Transmitting => _microphoneClip != null && Time.unscaledTime - _lastSentAt < .2f;

    internal void Tick(bool active, VoiceChatMode mode, KeyCode pushToTalk, string inputDevice, float microphoneGain,
        float activationThreshold, float volume, Action<string> send)
    {
        inputDevice = string.IsNullOrWhiteSpace(inputDevice) ? null : inputDevice;
        if (!string.Equals(_requestedDevice, inputDevice, StringComparison.Ordinal))
        {
            StopMicrophone();
            _requestedDevice = inputDevice;
            _retryAt = 0f;
        }
        foreach (var playback in _playbacks.Values) playback.Volume = volume;
        var expired = new List<long>();
        foreach (var pair in _playbacks)
            if (!active || Time.unscaledTime - pair.Value.LastFrameAt > 5f) expired.Add(pair.Key);
        foreach (var id in expired) { _playbacks[id].Dispose(); _playbacks.Remove(id); }

        if (!active || mode == VoiceChatMode.PushToTalk && (pushToTalk == KeyCode.None || !Input.GetKey(pushToTalk)))
        {
            StopMicrophone();
            return;
        }
        if (ReadMicrophone == null || Time.unscaledTime < _retryAt) return;
        if (_microphoneClip == null)
        {
            try
            {
                var devices = Microphone.devices;
                if (devices == null || devices.Length == 0 || inputDevice != null
                    && Array.IndexOf(devices, inputDevice) < 0) { _retryAt = Time.unscaledTime + 3f; return; }
                _activeDevice = inputDevice;
                _microphoneClip = Microphone.Start(_activeDevice, true, 1, VoiceCodec.SampleRate);
                _readPosition = 0;
                if (_microphoneClip == null) { _activeDevice = null; _retryAt = Time.unscaledTime + 3f; }
            }
            catch (Exception) { _activeDevice = null; _retryAt = Time.unscaledTime + 3f; }
            return;
        }
        try
        {
            var current = Microphone.GetPosition(_activeDevice);
            if (current < 0) return;
            var available = (current - _readPosition + VoiceCodec.SampleRate) % VoiceCodec.SampleRate;
            if (available > VoiceCodec.SampleRate / 2)
            {
                _readPosition = (current - VoiceCodec.FrameSamples + VoiceCodec.SampleRate) % VoiceCodec.SampleRate;
                available = VoiceCodec.FrameSamples;
            }
            for (var count = 0; available >= VoiceCodec.FrameSamples && count < 2; count++)
            {
                var samples = new float[VoiceCodec.FrameSamples];
                if (!ReadMicrophone(_microphoneClip, samples, _readPosition)) break;
                _readPosition = (_readPosition + VoiceCodec.FrameSamples) % VoiceCodec.SampleRate;
                available -= VoiceCodec.FrameSamples;
                var energy = 0f;
                for (var index = 0; index < samples.Length; index++)
                {
                    samples[index] = Mathf.Clamp(samples[index] * microphoneGain, -1f, 1f);
                    energy += samples[index] * samples[index];
                }
                if (mode == VoiceChatMode.VoiceActivation)
                {
                    if (Mathf.Sqrt(energy / samples.Length) >= activationThreshold)
                        _voiceGateUntil = Time.unscaledTime + .3f;
                    if (Time.unscaledTime > _voiceGateUntil) continue;
                }
                send(Convert.ToBase64String(VoiceCodec.Encode(samples)));
                _lastSentAt = Time.unscaledTime;
            }
        }
        catch (Exception)
        {
            StopMicrophone();
            _retryAt = Time.unscaledTime + 3f;
        }
    }

    internal void Receive(string encoded, float range, float volume)
    {
        if (encoded == null || encoded.Length > 900) return;
        byte[] packet;
        try { packet = Convert.FromBase64String(encoded); }
        catch (FormatException) { return; }
        if (!VoiceCodec.TryReadRelay(packet, out var sender, out var x, out var y, out var z, out var frame)) return;
        try
        {
            if (!_playbacks.TryGetValue(sender, out var playback))
            {
                playback = new Playback(sender, range);
                _playbacks.Add(sender, playback);
            }
            playback.Add(frame, new Vector3(x, y, z), volume);
        }
        catch (Exception) { /* A failed audio device should not abort the game RPC loop. */ }
    }

    private void StopMicrophone()
    {
        if (_microphoneClip == null) return;
        try { Microphone.End(_activeDevice); } catch (Exception) { }
        UnityEngine.Object.Destroy(_microphoneClip);
        _microphoneClip = null;
        _activeDevice = null;
        _readPosition = 0;
        _voiceGateUntil = 0f;
    }

    internal void Reset()
    {
        StopMicrophone();
        foreach (var playback in _playbacks.Values) playback.Dispose();
        _playbacks.Clear();
        _retryAt = 0f;
        _lastSentAt = 0f;
    }

    public void Dispose() => Reset();

    private static Func<AudioClip, float[], int, bool> CreateReader()
    {
        try
        {
            var method = typeof(AudioClip).GetMethod("GetData", new[] { typeof(float[]), typeof(int) });
            return method == null ? null : (Func<AudioClip, float[], int, bool>)Delegate.CreateDelegate(
                typeof(Func<AudioClip, float[], int, bool>), method);
        }
        catch (Exception) { return null; }
    }

    private sealed class Playback : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<float> _samples = new();
        private readonly GameObject _object;
        private readonly AudioSource _source;
        private readonly AudioClip _clip;
        internal float LastFrameAt { get; private set; }
        internal float Volume { set => _source.volume = Mathf.Clamp(value, 0f, 2f); }

        internal Playback(long sender, float range)
        {
            _object = new GameObject("VSM Voice " + sender);
            _source = _object.AddComponent<AudioSource>();
            _source.spatialBlend = 1f;
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.minDistance = 2f;
            _source.maxDistance = Mathf.Clamp(range, 5f, 100f);
            _source.dopplerLevel = 0f;
            _source.loop = true;
            _clip = AudioClip.Create("VSM Voice Stream", VoiceCodec.SampleRate, 1, VoiceCodec.SampleRate, true, Read);
            _source.clip = _clip;
        }

        internal void Add(byte[] frame, Vector3 position, float volume)
        {
            LastFrameAt = Time.unscaledTime;
            _object.transform.position = position;
            Volume = volume;
            bool start;
            lock (_gate)
            {
                for (var index = 0; index < frame.Length; index++) _samples.Enqueue(VoiceCodec.Decode(frame[index]));
                while (_samples.Count > VoiceCodec.FrameSamples * 6) _samples.Dequeue();
                start = !_source.isPlaying && _samples.Count >= VoiceCodec.FrameSamples * 2;
            }
            if (start) _source.Play();
        }

        private void Read(float[] data)
        {
            lock (_gate)
                for (var index = 0; index < data.Length; index++) data[index] = _samples.Count > 0 ? _samples.Dequeue() : 0f;
        }

        public void Dispose()
        {
            _source.Stop();
            UnityEngine.Object.Destroy(_clip);
            UnityEngine.Object.Destroy(_object);
        }
    }
}
