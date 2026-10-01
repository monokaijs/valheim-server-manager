namespace UnityEngine;

// Synthetic Unity boundaries only. VoiceChatClient.cs and VoiceCodec.cs are linked
// unchanged from production; these fixtures never open a native device or game.
internal static class VoiceAudioFixture
{
    internal static void Reset()
    {
        Time.unscaledTime = 10;
        Input.Pressed = false; AudioSource.PlayThrows = false; AudioClip.CreateFails = false; AudioClip.Prefill = false;
        GameObject.Objects.Clear();
        Microphone.devices = ["first", "second"];
        Microphone.Position = Microphone.Starts = Microphone.Ends = 0;
        Microphone.StartThrows = Microphone.PositionThrows = false;
        Microphone.LastStarted = null;
        Microphone.StartDevices.Clear(); Microphone.EndDevices.Clear();
        Microphone.StartClip = _ => Signal();
    }

    internal static AudioClip Signal(int channels = 1, int capacity = 16000, bool silence = false)
    {
        var data = new float[channels * capacity];
        for (var sample = 0; sample < capacity; sample++)
            for (var channel = 0; channel < channels; channel++)
                data[sample * channels + channel] = silence ? 0 : ChannelSample(sample, channel);
        return new AudioClip { Data = data, channels = channels, samples = capacity };
    }
    internal static float ChannelSample(int sample, int channel) =>
        (float)((channel % 2 == 0 ? .6 : .2) * Math.Sin(sample * 2 * Math.PI * (channel % 2 == 0 ? 440 : 660) / 16000));
    internal static float MonoSample(AudioClip clip, int sample) =>
        Enumerable.Range(0, clip.channels).Average(channel => clip.Data[(sample % clip.samples) * clip.channels + channel]);
}

internal class Object
{
    internal bool Destroyed;
    internal static void Destroy(Object? obj) { if (obj != null) obj.Destroyed = true; }
}
internal readonly struct Vector3(float x, float y, float z)
{
    internal readonly float x = x, y = y, z = z;
}
internal sealed class Transform { internal Vector3 position; }
internal sealed class GameObject : Object
{
    internal static readonly List<GameObject> Objects = new();
    internal readonly Transform transform = new();
    internal AudioSource Source = null!;
    internal GameObject(string name) { Objects.Add(this); }
    internal T AddComponent<T>() where T : new()
    {
        var component = new T();
        if (component is AudioSource source) Source = source;
        return component;
    }
}
internal sealed class AudioClip : Object
{
    internal static bool CreateFails, Prefill;
    internal float[] Data = [];
    internal int channels = 1, samples = 16000, frequency = 16000;
    internal bool Readable = true;
    internal Action<float[]> Callback = _ => { };
    internal readonly List<(int Offset, int Values)> Reads = new();
    // Must be public: production reflection resolves this exact Unity signature.
    public bool GetData(float[] data, int offset)
    {
        Reads.Add((offset, data.Length));
        if (!Readable) return false;
        for (var index = 0; index < data.Length; index++) data[index] = Data[(offset * channels + index) % Data.Length];
        return true;
    }
    internal static AudioClip Create(string name, int count, int channels, int rate, bool stream, Action<float[]> callback)
    {
        if (CreateFails) return null!;
        var clip=new AudioClip {samples=count,channels=channels,frequency=rate,Callback=callback};
        if (Prefill) callback(new float[count*channels]);
        return clip;
    }
}
internal enum AudioRolloffMode { Linear }
internal sealed class AudioSource : Object
{
    internal static bool PlayThrows;
    public AudioSource() { }
    internal float volume, spatialBlend, minDistance, maxDistance, dopplerLevel;
    internal bool loop, isPlaying, playOnAwake;
    internal bool bypassReverbZones, bypassListenerEffects, bypassEffects;
    internal float reverbZoneMix = 1f;
    internal AudioRolloffMode rolloffMode;
    internal AudioClip clip = null!;
    // Synthetic spatial boundary following Unity Linear rolloff; production
    // source configuration and positions are exercised, native mixing is not.
    internal float GainAt(Vector3 source, Vector3 listener)
    {
        var dx=source.x-listener.x; var dy=source.y-listener.y; var dz=source.z-listener.z;
        var distance=(float)Math.Sqrt(dx*dx+dy*dy+dz*dz);
        return volume*Math.Clamp((maxDistance-distance)/(maxDistance-minDistance),0f,1f);
    }
    internal void Play() { if (PlayThrows) throw new InvalidOperationException("synthetic output failure"); isPlaying = true; }
    internal void Stop() { isPlaying = false; }
}
internal static class Microphone
{
    internal static string[] devices = [];
    internal static int Position, Starts, Ends;
    internal static bool StartThrows, PositionThrows;
    internal static AudioClip? LastStarted;
    internal static Func<string?, AudioClip?> StartClip = _ => null;
    internal static readonly List<string?> StartDevices = new(), EndDevices = new();
    internal static AudioClip? Start(string? device, bool loop, int seconds, int rate)
    {
        Starts++; StartDevices.Add(device);
        if (StartThrows) throw new InvalidOperationException("synthetic startup failure");
        return LastStarted = StartClip(device);
    }
    internal static int GetPosition(string? device)
    {
        if (PositionThrows) throw new InvalidOperationException("synthetic capture failure");
        return Position;
    }
    internal static void End(string? device) { Ends++; EndDevices.Add(device); }
}
internal static class Time { internal static float unscaledTime; }
internal enum KeyCode { None, LeftAlt }
internal static class Input { internal static bool Pressed; internal static bool GetKey(KeyCode key) => Pressed; }
internal static class Mathf
{
    internal static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    internal static float Sqrt(float value) => (float)Math.Sqrt(value);
}
