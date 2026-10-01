using UnityEngine.EventSystems;
using ValheimServerManager.Client;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;
public sealed class VoiceSliderTests
{
    [Theory]
    [InlineData((int)VoiceSliderKind.Playback,1f,"100%")]
    [InlineData((int)VoiceSliderKind.Gain,1f,"1.00×")]
    [InlineData((int)VoiceSliderKind.Threshold,.015f,"1.5% RMS")]
    public void ValuesHaveReadableUnitsAndFiniteDefaults(int kind,float expected,string text)
    {
        var spec=new VoiceSliderSpec((VoiceSliderKind)kind);
        Assert.Equal(expected,spec.Clamp(float.NaN)); Assert.Equal(expected,spec.Clamp(float.PositiveInfinity));
        Assert.Equal(text,spec.Format(expected));
        Assert.Equal(spec.Minimum,spec.Clamp(-100)); Assert.Equal(spec.Maximum,spec.Clamp(100));
    }
    [Theory]
    [InlineData((int)VoiceSliderKind.Playback)]
    [InlineData((int)VoiceSliderKind.Gain)]
    [InlineData((int)VoiceSliderKind.Threshold)]
    public void ActualMoveHandlerUsesSmallStepsClampsAndLeavesVerticalFocusNative(int kind)
    {
        var spec=new VoiceSliderSpec((VoiceSliderKind)kind);
        var slider=new VoiceSlider {minValue=spec.Minimum,maxValue=spec.Maximum,Step=spec.Step,value=spec.Default};
        var right=new AxisEventData(MoveDirection.Right); slider.OnMove(right);
        Assert.Equal(spec.Default+spec.Step,slider.value,5); Assert.True(right.Used);
        var up=new AxisEventData(MoveDirection.Up); slider.OnMove(up); Assert.Equal(MoveDirection.Up,slider.NativeMove); Assert.False(up.Used);
        slider.value=spec.Maximum; slider.OnMove(new(MoveDirection.Right)); Assert.Equal(spec.Maximum,slider.value);
        slider.value=spec.Minimum; slider.OnMove(new(MoveDirection.Left)); Assert.Equal(spec.Minimum,slider.value);
        slider.Interactable=false; slider.OnMove(new(MoveDirection.Right)); Assert.Equal(spec.Minimum,slider.value);
    }
}
