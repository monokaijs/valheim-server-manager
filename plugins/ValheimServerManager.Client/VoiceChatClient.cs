#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using ValheimServerManager.VoiceSupport;

namespace ValheimServerManager.Client;

internal enum VoiceChatMode { PushToTalk, VoiceActivation, OpenMic }

internal sealed class VoiceChatClient : IDisposable
{
    // Unity 6 also exposes a Span overload that cannot be resolved by this net48 plugin.
    private static readonly Func<AudioClip, float[], int, bool> ReadMicrophone = CreateReader();
    private const float CaptureTimeout = 2f;
    private const float ReadFailureTimeout = .5f;
    private const float RetryDelay = 3f;
    private AudioClip _microphoneClip;
    private string _requestedDevice;
    private string _activeDevice;
    private int _readPosition;
    private int _lastCapturePosition;
    private float _lastCaptureProgressAt;
    private float _readFailureAt = -1f;
    private bool _ownsMicrophone;
    private bool _microphoneUnavailable;
    private float _retryAt;
    private float _voiceGateUntil;
    private float _lastSentAt = float.NegativeInfinity;
    private readonly Dictionary<long, Playback> _playbacks = new();
    internal readonly VoiceLevel Level = new();
    internal long CapturedFrames, EncodedFrames, SentFrames, SendDrops, ReceivedFrames, DecodedFrames,
        InvalidPackets, ReceiveDrops, CaptureFailures, PlaybackFailures, PlaybackStarts, OutputSamples, Underruns, ClippedFrames, RebufferWaits, TrimmedSamples, CallbackSamplesMax;
    internal string CaptureStatus { get; private set; } = "Microphone idle";
    internal string TransportStatus { get; private set; } = "No frames sent";
    internal string PlaybackStatus { get; private set; } = "No voice received";
    private float _nextDiagnostic;
    private string _lastDiagnostic;
    internal bool Speaking => Transmitting && Level.Speaking(Time.unscaledTime);
    internal int WaveCount => Speaking ? Level.Waves(Time.unscaledTime) : 0;
    internal string Summary => $"capture={CapturedFrames} encode={EncodedFrames} sent={SentFrames} dropped={SendDrops} receive={ReceivedFrames} receiveDrop={ReceiveDrops} decode={DecodedFrames} start={PlaybackStarts} outputSamples={Interlocked.Read(ref OutputSamples)} underruns={Interlocked.Read(ref Underruns)} rebufferWaits={Interlocked.Read(ref RebufferWaits)} trimmedSamples={Interlocked.Read(ref TrimmedSamples)} callbackSamplesMax={Interlocked.Read(ref CallbackSamplesMax)} invalid={InvalidPackets} captureFail={CaptureFailures} outputFail={PlaybackFailures} clipped={ClippedFrames}";
    internal void Diagnose(Action<string> log, string gate)
    {
        if (Time.unscaledTime < _nextDiagnostic) return;
        var value = gate + "; " + CaptureStatus + "; " + TransportStatus + "; " + PlaybackStatus + "; " + Summary;
        if (value == _lastDiagnostic) return;
        _nextDiagnostic = Time.unscaledTime + 10f; _lastDiagnostic = value;
        log("Voice flow: " + value);
    }
    internal bool MicrophoneUnavailable => ReadMicrophone == null || _microphoneUnavailable;
    internal bool Transmitting => _microphoneClip != null && Time.unscaledTime - _lastSentAt < .2f;

    internal void Tick(bool active, VoiceChatMode mode, KeyCode pushToTalk, string inputDevice, float microphoneGain,
        float activationThreshold, float volume, Func<string, bool> send, bool captureAllowed = true)
    {
        inputDevice = string.IsNullOrWhiteSpace(inputDevice) ? null : inputDevice;
        if (!string.Equals(_requestedDevice, inputDevice, StringComparison.Ordinal))
        {
            StopMicrophone();
            _requestedDevice = inputDevice;
            _retryAt = 0f;
            _microphoneUnavailable = false;
        }
        var expired = new List<long>();
        foreach (var pair in _playbacks)
            try { pair.Value.Volume = volume; }
            catch (Exception error)
            { PlaybackFailures++; PlaybackStatus = "Playback failed (" + error.GetType().Name + "); check output device"; expired.Add(pair.Key); }
        foreach (var pair in _playbacks)
            if ((!active || Time.unscaledTime - pair.Value.LastFrameAt > 5f) && !expired.Contains(pair.Key)) expired.Add(pair.Key);
        foreach (var id in expired) { _playbacks[id].Dispose(); _playbacks.Remove(id); }

        if (!active || !captureAllowed || mode == VoiceChatMode.PushToTalk && (pushToTalk == KeyCode.None || !Input.GetKey(pushToTalk)))
        {
            StopMicrophone();
            if (!MicrophoneUnavailable) CaptureStatus = !captureAllowed ? "Capture paused while binding a key" : active ? "Hold the push-to-talk key to capture" : "Microphone idle";
            return;
        }
        if (ReadMicrophone == null) { CaptureStatus = "Capture API unavailable; check the companion/game versions"; return; }
        if (Time.unscaledTime < _retryAt) return;
        if (_microphoneClip == null)
        {
            try
            {
                var devices = Microphone.devices;
                if (devices == null || devices.Length == 0 || inputDevice != null
                    && Array.IndexOf(devices, inputDevice) < 0) { FailMicrophone("No selected input; choose a microphone and check system permission"); return; }
                _activeDevice = inputDevice;
                _ownsMicrophone = true;
                _microphoneClip = Microphone.Start(_activeDevice, true, 1, VoiceCodec.SampleRate);
                _readPosition = 0;
                _lastCapturePosition = 0;
                _lastCaptureProgressAt = Time.unscaledTime;
                CaptureStatus = "Starting capture; waiting for samples";
                if (_microphoneClip == null || _microphoneClip.channels < 1 || _microphoneClip.channels > 32
                    || _microphoneClip.samples < VoiceCodec.FrameSamples * 2 || _microphoneClip.frequency != VoiceCodec.SampleRate)
                    FailMicrophone("Unsupported input format; choose another device");
            }
            catch (Exception error) { FailMicrophone("Capture start failed (" + error.GetType().Name + "); check microphone permission/device"); }
            return;
        }
        try
        {
            var current = Microphone.GetPosition(_activeDevice);
            var capacity = _microphoneClip.samples;
            if (current < 0 || current >= capacity)
            {
                _microphoneUnavailable = true;
                CaptureStatus = "Invalid capture cursor; waiting for recovery";
                Level.Reset();
                if (Time.unscaledTime - _lastCaptureProgressAt >= CaptureTimeout) FailMicrophone("Capture cursor invalid; check microphone device");
                return;
            }
            if (current != _lastCapturePosition)
            {
                _lastCapturePosition = current;
                _lastCaptureProgressAt = Time.unscaledTime;
            }
            else if (Time.unscaledTime - _lastCaptureProgressAt >= CaptureTimeout)
            {
                FailMicrophone("Input stalled; check microphone permission/device");
                return;
            }
            var available = (current - _readPosition + capacity) % capacity;
            if (available > capacity / 2)
            {
                _readPosition = (current - VoiceCodec.FrameSamples + capacity) % capacity;
                available = VoiceCodec.FrameSamples;
            }
            var channels = _microphoneClip.channels;
            for (var count = 0; available >= VoiceCodec.FrameSamples && count < 8; count++)
            {
                // Unity offsets/positions count sample frames; GetData returns interleaved
                // channel values. Read a full frame per channel before downmixing to mono.
                var interleaved = new float[VoiceCodec.FrameSamples * channels];
                if (!ReadMicrophone(_microphoneClip, interleaved, _readPosition))
                {
                    _microphoneUnavailable = true; Level.Reset();
                    CaptureStatus = "Input read failed; waiting for recovery";
                    if (_readFailureAt < 0f) _readFailureAt = Time.unscaledTime;
                    if (Time.unscaledTime - _readFailureAt >= ReadFailureTimeout) FailMicrophone("Input read failed; check microphone permission/device");
                    break;
                }
                _readFailureAt = -1f;
                _microphoneUnavailable = false;
                var samples = new float[VoiceCodec.FrameSamples];
                _readPosition = (_readPosition + VoiceCodec.FrameSamples) % capacity;
                available -= VoiceCodec.FrameSamples;
                var energy = 0f;
                var clipped = false;
                for (var index = 0; index < samples.Length; index++)
                {
                    var mixed = 0f;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var value = interleaved[index * channels + channel];
                        if (Finite(value)) mixed += Mathf.Clamp(value, -1f, 1f) / channels;
                    }
                    var amplified = mixed * (Finite(microphoneGain) ? microphoneGain : 1f);
                    clipped |= Math.Abs(amplified) >= .99f;
                    samples[index] = Mathf.Clamp(amplified, -1f, 1f);
                    energy += samples[index] * samples[index];
                }
                CapturedFrames++;
                var rms = Mathf.Sqrt(energy / samples.Length);
                Level.Add(rms, clipped, activationThreshold, Time.unscaledTime);
                if (clipped) ClippedFrames++;
                CaptureStatus = clipped ? "Input clipping; reduce microphone gain" : rms < .001f ? "Input silent; check device/mute if speaking" : "Input samples captured";
                if (mode == VoiceChatMode.VoiceActivation)
                {
                    if (rms >= (Finite(activationThreshold) ? Mathf.Clamp(activationThreshold, .001f, .2f) : .015f))
                        _voiceGateUntil = Time.unscaledTime + .3f;
                    if (Time.unscaledTime > _voiceGateUntil) continue;
                }
                var encoded = Convert.ToBase64String(VoiceCodec.Encode(samples));
                EncodedFrames++;
                try
                {
                    if (send(encoded))
                    { SentFrames++; _lastSentAt = Time.unscaledTime; TransportStatus = "Frames queued to server (delivery unconfirmed)"; }
                    else { SendDrops++; TransportStatus = "Send skipped; disconnected or queue busy"; }
                }
                catch (Exception error)
                { SendDrops++; TransportStatus = "Send failed (" + error.GetType().Name + "); check connection"; }
            }
        }
        catch (Exception error)
        {
            FailMicrophone("Capture failed (" + error.GetType().Name + "); check input device");
        }
    }

    internal void SetPlaybackRange(float range)
    {
        foreach (var playback in _playbacks.Values)
            try { playback.Range = range; }
            catch (Exception error) { PlaybackFailures++; PlaybackStatus = "Playback range failed (" + error.GetType().Name + "); stream will retry"; }
    }

    internal void Receive(string encoded, float range, float volume)
    {
        ReceivedFrames++;
        if (encoded == null || encoded.Length > 900) { InvalidPacket(); return; }
        byte[] packet;
        try { packet = Convert.FromBase64String(encoded); }
        catch (FormatException) { InvalidPacket(); return; }
        if (!VoiceCodec.TryReadRelay(packet, out var sender, out var x, out var y, out var z, out var frame)) { InvalidPacket(); return; }
        try
        {
            if (!_playbacks.TryGetValue(sender, out var playback))
            {
                playback = new Playback(this, sender, range);
                _playbacks.Add(sender, playback);
            }
            playback.Range = range;
            playback.Add(frame, new Vector3(x, y, z), volume);
            PlaybackStatus = volume <= 0 ? "Playback volume is zero" : "Voice decoded; awaiting/feeding audio output";
        }
        catch (Exception error)
        {
            PlaybackFailures++; PlaybackStatus = "Playback failed (" + error.GetType().Name + "); check output device";
            if (_playbacks.TryGetValue(sender, out var failed)) { failed.Dispose(); _playbacks.Remove(sender); }
        }
    }

    internal void RejectReceive() { ReceiveDrops++; PlaybackStatus = "Receive blocked by local mute/server policy/session state"; }

    private void InvalidPacket() { InvalidPackets++; PlaybackStatus = "Invalid voice packet; check matching companion versions"; }

    private void StopMicrophone()
    {
        var clip = _microphoneClip;
        _microphoneClip = null;
        if (_ownsMicrophone) { try { Microphone.End(_activeDevice); } catch (Exception) { } }
        _ownsMicrophone = false;
        if (clip != null) { try { UnityEngine.Object.Destroy(clip); } catch (Exception) { } }
        _activeDevice = null;
        _readPosition = 0;
        _readFailureAt = -1f;
        _voiceGateUntil = 0f;
        _lastSentAt = float.NegativeInfinity;
        Level.Reset();
    }

    private void FailMicrophone(string status)
    {
        StopMicrophone();
        _microphoneUnavailable = true;
        CaptureFailures++; CaptureStatus = status + "; retry in 3 seconds";
        _retryAt = Time.unscaledTime + RetryDelay;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    internal void Reset()
    {
        StopMicrophone();
        foreach (var playback in _playbacks.Values) playback.Dispose();
        _playbacks.Clear();
        _retryAt = 0f;
        _microphoneUnavailable = false;
        CaptureStatus = "Microphone idle"; TransportStatus = "No frames sent"; PlaybackStatus = "No voice received";
        CapturedFrames = EncodedFrames = SentFrames = SendDrops = ReceivedFrames = DecodedFrames = InvalidPackets =
            ReceiveDrops = CaptureFailures = PlaybackFailures = PlaybackStarts = ClippedFrames = 0;
        Interlocked.Exchange(ref OutputSamples, 0); Interlocked.Exchange(ref Underruns, 0);
        Interlocked.Exchange(ref RebufferWaits, 0); Interlocked.Exchange(ref TrimmedSamples, 0); Interlocked.Exchange(ref CallbackSamplesMax, 0);
        _nextDiagnostic = 0; _lastDiagnostic = null;
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
        private readonly VoiceChatClient _owner;
        private readonly object _gate = new();
        private readonly VoicePlaybackBuffer _buffer = new();
        private readonly GameObject _object;
        private readonly AudioSource _source;
        private readonly AudioClip _clip;
        internal float LastFrameAt { get; private set; }
        internal float Volume { set { if (Finite(value)) _source.volume = Mathf.Clamp(value, 0f, 2f); } }
        internal float Range { set { if (Finite(value)) _source.maxDistance = Mathf.Clamp(value, 5f, 100f); } }

        internal Playback(VoiceChatClient owner, long sender, float range)
        {
            _owner = owner;
            try
            {
                _object = new GameObject("VSM Voice " + sender);
                _source = _object.AddComponent<AudioSource>();
                _source.spatialBlend = 1f;
                _source.rolloffMode = AudioRolloffMode.Linear;
                _source.minDistance = 2f;
                _source.maxDistance = 40f;
                _source.volume = 1f;
                Range = range;
                _source.dopplerLevel = 0f;
                // Voice stays positional but must not inherit environmental tails.
                _source.bypassReverbZones = true;
                _source.reverbZoneMix = 0f;
                _source.bypassListenerEffects = true;
                _source.bypassEffects = true;
                _source.loop = true;
                _source.playOnAwake = false;
                _clip = AudioClip.Create("VSM Voice Stream", VoiceCodec.FrameSamples * 4, 1, VoiceCodec.SampleRate, true, Read);
                if (_clip == null) throw new InvalidOperationException("Voice stream clip unavailable");
                _source.clip = _clip;
            }
            catch { Dispose(); throw; }
        }

        internal void Add(byte[] frame, Vector3 position, float volume)
        {
            var resumedAfterPause = Time.unscaledTime - LastFrameAt > .5f;
            LastFrameAt = Time.unscaledTime;
            _object.transform.position = position;
            Volume = volume;
            bool start;
            lock (_gate)
            {
                if (resumedAfterPause) { Interlocked.Add(ref _owner.TrimmedSamples, _buffer.Count); _buffer.Clear(); }
                Interlocked.Add(ref _owner.TrimmedSamples, _buffer.Add(frame));
                _owner.DecodedFrames++;
                start = !_source.isPlaying && _buffer.Count >= VoicePlaybackBuffer.InitialSamples;
            }
            if (start) { _source.Play(); _owner.PlaybackStarts++; }
        }

        private void Read(float[] data)
        {
            int consumed; bool underrun, waiting;
            lock (_gate) consumed = _buffer.Read(data, out underrun, out waiting);
            Interlocked.Add(ref _owner.OutputSamples, consumed);
            if (underrun) Interlocked.Increment(ref _owner.Underruns);
            if (waiting) Interlocked.Increment(ref _owner.RebufferWaits);
            long previous;
            do { previous = Interlocked.Read(ref _owner.CallbackSamplesMax); if (data.Length <= previous) break; }
            while (Interlocked.CompareExchange(ref _owner.CallbackSamplesMax, data.Length, previous) != previous);
        }

        public void Dispose()
        {
            try { if (_source != null) _source.Stop(); } catch (Exception) { }
            if (_clip != null) UnityEngine.Object.Destroy(_clip);
            if (_object != null) UnityEngine.Object.Destroy(_object);
        }
    }
}
