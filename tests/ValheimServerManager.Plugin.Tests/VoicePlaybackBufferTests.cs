using UnityEngine;
using ValheimServerManager.Client;
using ValheimServerManager.VoiceSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

[Collection("Voice Unity fixture")]
public sealed class VoicePlaybackBufferTests
{
    private static byte[] Frame(float value=.4f) => VoiceCodec.Encode(Enumerable.Repeat(value,640).ToArray());
    [Theory]
    [InlineData(640)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void PrimingAdaptsToNativeCallbackWithoutConsumingPartialCushion(int requested)
    {
        var buffer=new VoicePlaybackBuffer(); buffer.Add(Frame()); buffer.Add(Frame());
        var output=new float[requested]; Array.Fill(output,1);
        Assert.Equal(0,buffer.Read(output,out var underrun,out var waiting));
        Assert.True(waiting); Assert.False(underrun); Assert.All(output,x=>Assert.Equal(0,x)); Assert.Equal(1280,buffer.Count);
        while(buffer.Count < buffer.Target) buffer.Add(Frame());
        Assert.Equal(requested,buffer.Read(output,out underrun,out waiting));
        Assert.False(underrun); Assert.False(waiting); Assert.All(output,x=>Assert.InRange(x,.38f,.42f));
    }
    [Fact]
    public void StarvedPlaybackPrimesAgainAndNeverRepeatsOldVoice()
    {
        var buffer=new VoicePlaybackBuffer(); for(int i=0;i<3;i++) buffer.Add(Frame());
        buffer.Read(new float[1280],out _,out _);
        var tail=new float[1280]; Assert.Equal(640,buffer.Read(tail,out var underrun,out _));
        Assert.True(underrun); Assert.Equal(0,tail[639]); Assert.All(tail.Skip(640),x=>Assert.Equal(0,x));
        buffer.Add(Frame(.2f)); var output=new float[640];
        Assert.Equal(0,buffer.Read(output,out underrun,out var waiting)); Assert.True(waiting); Assert.False(underrun);
        buffer.Add(Frame(.2f)); buffer.Add(Frame(.2f)); buffer.Add(Frame(.2f));
        Assert.Equal(640,buffer.Read(output,out underrun,out waiting)); Assert.All(output,x=>Assert.InRange(x,.19f,.21f));
    }
    [Fact]
    public void OverflowDropsWholeFramesAndRemainsBoundedEvenAfterPartialReads()
    {
        var buffer=new VoicePlaybackBuffer(); for(int i=0;i<12;i++) Assert.Equal(0,buffer.Add(Frame()));
        Assert.Equal(1024,buffer.Read(new float[1024],out _,out _));
        Assert.Equal(0,buffer.Add(Frame())); Assert.Equal(640,buffer.Add(Frame()));
        Assert.InRange(buffer.Count,VoicePlaybackBuffer.MaximumSamples-639,VoicePlaybackBuffer.MaximumSamples);
    }
    [Fact]
    public void DryVoiceKeepsPositionalAttenuationAndOneSourcePerSender()
    {
        VoiceAudioFixture.Reset(); using var client=new VoiceChatClient();
        var packet=Convert.ToBase64String(VoiceCodec.Relay(42,1,2,3,Frame()));
        for(int i=0;i<3;i++) client.Receive(packet,25,.5f);
        var source=GameObject.Objects.Single().Source;
        Assert.True(source.bypassReverbZones); Assert.True(source.bypassListenerEffects); Assert.True(source.bypassEffects);
        Assert.Equal(0,source.reverbZoneMix); Assert.Equal(1,source.spatialBlend);
        Assert.Equal(AudioRolloffMode.Linear,source.rolloffMode); Assert.Equal(2,source.minDistance); Assert.Equal(25,source.maxDistance);
        Assert.Equal(.5f,source.volume); Assert.True(source.isPlaying); Assert.False(source.playOnAwake);
    }
    [Fact]
    public void ProductionSpatialPositionsFollowSpeakerAndPolicyWhileListenerCanMove()
    {
        VoiceAudioFixture.Reset(); using var client=new VoiceChatClient();
        string Packet(float x)=>Convert.ToBase64String(VoiceCodec.Relay(42,x,0,0,Frame()));
        client.Receive(Packet(0),40,1);
        var obj=GameObject.Objects.Single(); var source=obj.Source;
        Assert.Equal(1,source.GainAt(obj.transform.position,new(2,0,0)));
        Assert.Equal(.5f,source.GainAt(obj.transform.position,new(21,0,0)),5);
        Assert.Equal(0,source.GainAt(obj.transform.position,new(40,0,0)));
        client.Receive(Packet(10),40,.5f);
        Assert.Equal(10,obj.transform.position.x); Assert.Equal(.5f,source.GainAt(obj.transform.position,new(12,0,0)));
        Assert.Equal(.25f,source.GainAt(obj.transform.position,new(31,0,0)),5);
        client.SetPlaybackRange(20);
        Assert.Equal(20,source.maxDistance); Assert.Equal(.25f,source.GainAt(obj.transform.position,new(21,0,0)),5);
        Assert.Equal(0,source.GainAt(obj.transform.position,new(30,0,0)));
    }
    [Fact]
    public void PauseDiscardsUnplayedOldTailBeforeNextUtterance()
    {
        VoiceAudioFixture.Reset(); using var client=new VoiceChatClient();
        string Packet(float value) => Convert.ToBase64String(VoiceCodec.Relay(42,1,0,0,Frame(value)));
        client.Receive(Packet(.4f),40,1);
        Time.unscaledTime += .6f;
        for(int i=0;i<3;i++) client.Receive(Packet(.2f),40,1);
        var output=new float[640]; GameObject.Objects.Single().Source.clip.Callback(output);
        Assert.All(output,x=>Assert.InRange(x,.19f,.21f)); Assert.Equal(640,client.TrimmedSamples);
    }
    [Fact]
    public void ClipSizedNativePrefillFitsBoundedQueueAndCanPrimeBeforeConsumption()
    {
        VoiceAudioFixture.Reset(); AudioClip.Prefill=true; using var client=new VoiceChatClient();
        var packet=Convert.ToBase64String(VoiceCodec.Relay(42,1,0,0,Frame()));
        client.Receive(packet,40,1); var source=GameObject.Objects.Single().Source;
        Assert.Equal(2560,source.clip.samples); Assert.Equal(2560,client.CallbackSamplesMax);
        Assert.Equal(1,client.RebufferWaits); Assert.Equal(0,client.Underruns);
        for(int i=0;i<4;i++) client.Receive(packet,40,1);
        var output=new float[source.clip.samples]; source.clip.Callback(output);
        Assert.Equal(2560,client.OutputSamples); Assert.All(output,x=>Assert.InRange(x,.38f,.42f)); Assert.Equal(0,client.Underruns);
    }
    [Fact]
    public void KeyBindingPausesCaptureWithoutDisposingReceivedVoice()
    {
        VoiceAudioFixture.Reset(); using var client=new VoiceChatClient();
        client.Tick(true,VoiceChatMode.OpenMic,KeyCode.LeftAlt,"",1,.015f,1,_=>true);
        var packet=Convert.ToBase64String(VoiceCodec.Relay(42,1,0,0,Frame()));
        for(int i=0;i<3;i++) client.Receive(packet,40,1);
        var obj=GameObject.Objects.Single();
        Time.unscaledTime += .04f; Microphone.Position=640;
        client.Tick(true,VoiceChatMode.OpenMic,KeyCode.LeftAlt,"",1,.015f,1,_=>throw new Exception("Unexpected send while binding"),captureAllowed:false);
        Assert.Equal(1,Microphone.Ends); Assert.Equal(0,client.SentFrames);
        Assert.False(obj.Destroyed); Assert.True(obj.Source.isPlaying); Assert.Contains("paused",client.CaptureStatus);
    }
    [Fact]
    public void SlowRenderTickCanCatchUpWithoutSkippingNormalSpeech()
    {
        VoiceAudioFixture.Reset(); using var client=new VoiceChatClient();
        client.Tick(true,VoiceChatMode.OpenMic,KeyCode.LeftAlt,"",1,.015f,1,_=>true);
        for(int i=1;i<=100;i++) {
            Time.unscaledTime=10+i*.1f; Microphone.Position=i*1600%16000;
            client.Tick(true,VoiceChatMode.OpenMic,KeyCode.LeftAlt,"",1,.015f,1,_=>true);
        }
        Assert.Equal(250,client.SentFrames); Assert.Equal(250,client.CapturedFrames);
    }
}
