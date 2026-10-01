using System;
using System.IO;
using UnityEngine;

namespace ValheimServerManager.Client;

// Transparent original PNGs are embedded in the companion DLL and loaded once.
internal static class VoiceIcon
{
    // Resolve byte[] overload explicitly: Unity 6 also has a Span overload that
    // cannot be referenced by this net48 assembly (same constraint as GetData).
    private static readonly Func<Texture2D, byte[], bool, bool> LoadPng = CreateLoader();
    private static Func<Texture2D, byte[], bool, bool> CreateLoader()
    {
        try
        {
            var method = typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
            return method == null ? null : (Func<Texture2D, byte[], bool, bool>)Delegate.CreateDelegate(typeof(Func<Texture2D, byte[], bool, bool>), method);
        }
        catch (Exception) { return null; }
    }
    private static readonly Texture2D[] Textures = new Texture2D[4];
    private static readonly Sprite[] Sprites = new Sprite[4];
    private static readonly bool[] Attempted = new bool[4];
    internal static Texture2D Get(int waves)
    {
        var index = Math.Max(0, Math.Min(3, waves));
        if (Attempted[index]) return Textures[index];
        Attempted[index] = true;
        Texture2D texture = null;
        try
        {
            using var stream = typeof(VoiceIcon).Assembly.GetManifestResourceStream(
                "ValheimServerManager.Client.Assets.Voice.microphone-" + index + ".png");
            if (stream == null) throw new InvalidDataException("Embedded voice PNG missing");
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            { name = "VSM voice PNG " + index, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (LoadPng == null || !LoadPng(texture, bytes.ToArray(), true)) throw new InvalidDataException("Embedded voice PNG invalid");
            Textures[index] = texture;
        }
        catch (Exception error)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            ClientPlugin.Instance?.LogVoiceUiWarning("Voice icon could not load (" + error.GetType().Name + "); reinstall the companion package.");
        }
        return Textures[index];
    }
    internal static Sprite GetSprite(int waves)
    {
        var index = Math.Max(0, Math.Min(3, waves));
        var texture = Get(index);
        if (Sprites[index] == null && texture != null)
            Sprites[index] = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f));
        return Sprites[index];
    }
    internal static void Dispose()
    {
        for (var index = 0; index < Textures.Length; index++)
        {
            if (Sprites[index] != null) UnityEngine.Object.Destroy(Sprites[index]);
            if (Textures[index] != null) UnityEngine.Object.Destroy(Textures[index]);
            Sprites[index] = null; Textures[index] = null; Attempted[index] = false;
        }
    }
}
