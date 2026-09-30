using ValheimServerManager.Client;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class VoicePresentationTests
{
    [Theory]
    [InlineData(false, true, true, true, false, true, (int)VoiceIndicatorState.Muted)]
    [InlineData(true, true, false, false, false, true, (int)VoiceIndicatorState.Muted)]
    [InlineData(true, false, false, false, false, false, (int)VoiceIndicatorState.Unavailable)]
    [InlineData(true, true, true, false, false, true, (int)VoiceIndicatorState.Unavailable)]
    [InlineData(true, true, true, true, true, true, (int)VoiceIndicatorState.Unavailable)]
    [InlineData(true, true, true, true, false, false, (int)VoiceIndicatorState.Ready)]
    [InlineData(true, true, true, true, false, true, (int)VoiceIndicatorState.Transmitting)]
    public void IndicatorDoesNotMistakeReadinessOrPolicyForTransmission(bool enabled, bool received,
        bool server, bool ready, bool unavailable, bool sending, int expected)
        => Assert.Equal((VoiceIndicatorState)expected, VoicePresentation.State(enabled, received, server, ready, unavailable, sending));

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(800, 600)]
    [InlineData(640, 360)]
    [InlineData(960, 540)] // native UI scale 200% at 1080p
    public void PanelFitsCanvasWithoutCropping(float width, float height)
    {
        var scale = VoicePresentation.PanelScale(width, height);
        Assert.InRange(620 * scale, 1, width - 32);
        Assert.InRange(680 * scale, 1, height - 32);
        Assert.InRange(scale, .1f, 1);
    }
}
