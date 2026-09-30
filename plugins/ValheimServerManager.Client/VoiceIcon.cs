using System;
using UnityEngine;

namespace ValheimServerManager.Client;

// Original vector-like silhouettes rasterized once. No bundled/extracted game artwork.
internal static class VoiceIcon
{
    private static readonly Texture2D[] Textures = new Texture2D[4];
    internal static Texture2D Get(VoiceIndicatorState state)
    {
        var index = (int)state;
        if (Textures[index] != null) return Textures[index];
        const int size = 96;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "VSM microphone " + state, hideFlags = HideFlags.HideAndDontSave };
        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            // Coordinates use a top-left origin for the icon path.
            var py = size - 1 - y;
            var capsule = x >= 34 && x <= 54 && py >= 18 && py <= 49
                || Distance(x, py, 44, 18) <= 10 || Distance(x, py, 44, 49) <= 10;
            var arc = py >= 43 && py <= 68 && Math.Abs(Distance(x, py, 44, 43) - 24) <= 2.5f;
            var stand = x >= 41 && x <= 47 && py >= 68 && py <= 81 || x >= 30 && x <= 58 && py >= 79 && py <= 84;
            var extra = state == VoiceIndicatorState.Muted && Segment(x, py, 14, 16, 76, 84, 4)
                || state == VoiceIndicatorState.Transmitting && (Segment(x, py, 76, 30, 83, 37, 2.5f)
                    || Segment(x, py, 83, 37, 83, 55, 2.5f) || Segment(x, py, 83, 55, 76, 62, 2.5f))
                || state == VoiceIndicatorState.Unavailable && (x >= 76 && x <= 82 && py >= 21 && py <= 48
                    || Distance(x, py, 79, 60) <= 4);
            // Knock out a channel around the muted slash for non-color recognition.
            var knockout = state == VoiceIndicatorState.Muted && Segment(x, py, 14, 16, 76, 84, 7);
            pixels[y * size + x] = extra || (capsule || arc || stand) && !knockout ? Color.white : Color.clear;
        }
        texture.SetPixels(pixels); texture.Apply(false, true); Textures[index] = texture;
        return texture;
    }
    private static float Distance(float x, float y, float cx, float cy) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
    private static bool Segment(float x, float y, float ax, float ay, float bx, float by, float radius)
    {
        var t = Mathf.Clamp01(((x - ax) * (bx - ax) + (y - ay) * (by - ay)) / ((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
        return Distance(x, y, ax + t * (bx - ax), ay + t * (by - ay)) <= radius;
    }
    internal static void Dispose()
    {
        for (var index = 0; index < Textures.Length; index++)
        { if (Textures[index] != null) UnityEngine.Object.Destroy(Textures[index]); Textures[index] = null; }
    }
}
