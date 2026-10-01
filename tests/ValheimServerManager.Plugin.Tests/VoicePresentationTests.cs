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
        Assert.InRange(VoicePresentation.PanelWidth * scale, 1, width - 32);
        Assert.InRange(VoicePresentation.PanelHeight * scale, 1, height - 32);
        Assert.InRange(scale, .1f, 1);
    }

    [Theory]
    [InlineData((int)VoiceIndicatorState.Muted, true, false)]
    [InlineData((int)VoiceIndicatorState.Ready, true, false)]
    [InlineData((int)VoiceIndicatorState.Unavailable, true, false)]
    [InlineData((int)VoiceIndicatorState.Transmitting, false, false)]
    [InlineData((int)VoiceIndicatorState.Transmitting, true, true)]
    public void OnlyActualSpeechHasAHud(int state, bool speaking, bool visible)
        => Assert.Equal(visible, VoicePresentation.HudVisible((VoiceIndicatorState)state, speaking));

    [Fact]
    public void LoudnessHasThreeStableLevelsAndSilenceExpires()
    {
        var level = new VoiceLevel();
        level.Add(.018f, false, .015f, 10); Assert.Equal(1, level.Waves(10));
        level.Add(.05f, false, .015f, 10.04f); Assert.Equal(2, level.Waves(10.04f));
        level.Add(.2f, true, .015f, 10.08f); Assert.Equal(3, level.Waves(10.08f)); Assert.True(level.Clipping);
        level.Add(.085f, false, .015f, 10.12f); Assert.Equal(3, level.Waves(10.12f));
        level.Add(0, false, .015f, 10.16f); Assert.True(level.Speaking(10.16f));
        level.Add(0, false, .015f, 10.29f); Assert.False(level.Speaking(10.29f)); Assert.Equal(0, level.Waves(10.29f));
        Assert.Equal(0, level.Meter(10.6f));
        level.Reset(); Assert.Equal(0, level.Meter(10.6f)); Assert.False(level.Speaking(10.6f));
    }

    [Fact]
    public void LevelHysteresisDoesNotFlapAtThresholdOrAcceptNonFiniteInput()
    {
        var level = new VoiceLevel();
        level.Add(.015f, false, .015f, 10);
        level.Add(.014f, false, .015f, 10.04f); Assert.True(level.Speaking(10.04f));
        level.Add(float.NaN, false, float.NaN, 10.3f); Assert.False(level.Speaking(10.3f));
        level.Add(float.PositiveInfinity, false, .015f, 10.5f); Assert.Equal(0, level.Meter(10.5f));
    }

    [Theory]
    [InlineData("Valheim-Viking", false)]
    [InlineData("NORSE", false)]
    [InlineData("Arial", true)]
    [InlineData("Liberation Sans SDF", true)]
    [InlineData("", false)]
    public void BodyFallbackExcludesDecorativeFonts(string name, bool usable)
        => Assert.Equal(usable, VoicePresentation.ReadableFont(name));

    [Fact]
    public void ClosingAndReopeningModalRestoresCursorFocusOncePerSession()
    {
        var lifetime = new VoiceUiLifetime(); var restored = 0; var destroyed = 0;
        for (var index = 0; index < 3; index++)
        {
            lifetime.Begin(() => restored++); Assert.True(lifetime.IsOpen);
            lifetime.Close(() => destroyed++); lifetime.Close(() => destroyed++);
            Assert.False(lifetime.IsOpen);
        }
        Assert.Equal(3, restored); Assert.Equal(3, destroyed);
    }

    [Fact]
    public void PartialOpenOrTeardownFailureStillRestoresModalOwnership()
    {
        var lifetime = new VoiceUiLifetime(); var restored = false;
        lifetime.Begin(() => restored = true);
        Assert.Throws<InvalidOperationException>(() => lifetime.Begin(() => { }));
        Assert.Throws<IOException>(() => lifetime.Close(() => throw new IOException("synthetic teardown")));
        Assert.True(restored); Assert.False(lifetime.IsOpen);
        lifetime.Begin(() => { }); lifetime.Close(() => { }); Assert.False(lifetime.IsOpen);
    }
}
