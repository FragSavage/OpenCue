using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace OpenCue;

public sealed record RouteEndpoint(string Id,string Name,int Channels,int SampleRate,int ChannelMask,bool Virtual)
{
    public override string ToString()=>$"{Name} · {Channels}ch / {SampleRate/1000.0:0.#} kHz";
}

/// <summary>Explicit virtual-cable capture to physical-headphone playback. No recording or network output.</summary>
public sealed class RelayService : IDisposable
{
    MMDeviceEnumerator? enumerator;
    MMDevice? inputDevice,outputDevice;
    WasapiCapture? capture;
    WasapiOut? output;
    BufferedWaveProvider? buffer;
    volatile bool running;
    int generation;
    public bool Running=>running;
    public int Generation=>Volatile.Read(ref generation);
    public event Action<int,string>? Faulted;
    public StereoRelayProvider? Stereo {get;private set;}
    public int BufferMilliseconds=60;
    public int DropCount {get;private set;}
    public double BufferedMilliseconds=>buffer?.BufferedDuration.TotalMilliseconds??0;
    public static bool IsVirtualCable(string name,string driver)=>driver.Contains("VB-Audio",StringComparison.OrdinalIgnoreCase)||driver.Contains("VB Audio",StringComparison.OrdinalIgnoreCase)||name.Contains("Voicemeeter",StringComparison.OrdinalIgnoreCase)||name.StartsWith("Art Tune",StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<RouteEndpoint> List(bool captureEndpoints)
    {
        using var e=new MMDeviceEnumerator();var list=new List<RouteEndpoint>();
        foreach(var device in e.EnumerateAudioEndPoints(captureEndpoints?DataFlow.Capture:DataFlow.Render,DeviceState.Active))
        using(device)
        {
            using var client=device.AudioClient;var format=client.MixFormat;bool virtualCable=IsVirtualCable(device.FriendlyName,device.DeviceFriendlyName);
            if(captureEndpoints&&!virtualCable)continue;
            list.Add(new(device.ID,device.FriendlyName,format.Channels,format.SampleRate,ReadChannelMask(format),virtualCable));
        }
        return list.OrderBy(d=>d.Virtual).ThenBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static void ValidateSpatialEndpoint(string id)
    {
        using var e=new MMDeviceEnumerator();using var device=e.GetDevice(id);using var client=device.AudioClient;var format=client.MixFormat;
        if(device.DataFlow!=DataFlow.Render||!IsVirtualCable(device.FriendlyName,device.DeviceFriendlyName))throw new InvalidOperationException("7.1 rendering needs a separate virtual playback output such as VB-CABLE. Select that output in Sound.");
        if(format.Channels!=8||ReadChannelMask(format)!=0x63f)throw new InvalidOperationException("Configure the virtual playback output as standard 7.1 surround (eight channels), then Refresh. The selected output is not in the required 7.1 format.");
        if(format.SampleRate<32000||format.SampleRate>96000)throw new InvalidOperationException("Use a virtual-output format between 32 and 96 kHz; 48 kHz is recommended for games.");
    }
    public static void ValidateArtEndpoint(string id)
    {
        using var e=new MMDeviceEnumerator();using var device=e.GetDevice(id);using var client=device.AudioClient;var format=client.MixFormat;
        if(device.DataFlow!=DataFlow.Render||!IsVirtualCable(device.FriendlyName,device.DeviceFriendlyName)||format.Channels!=16)throw new InvalidOperationException("The official ArtTune profile needs its matching 16-channel virtual playback output. Select that output before applying.");
    }
    public static int ValidateToneEndpoint(string id,double maximumCorrectionFrequency)
    {
        using var e=new MMDeviceEnumerator();using var device=e.GetDevice(id);using var client=device.AudioClient;var format=client.MixFormat;
        if(device.DataFlow!=DataFlow.Render||format.Channels<2||format.SampleRate<44100)throw new InvalidOperationException("Choose a stereo playback output at 44.1 kHz or higher. A 48 kHz format is recommended for game audio.");
        if(maximumCorrectionFrequency>=format.SampleRate/2.0)throw new InvalidOperationException("The imported correction contains a frequency at or above this output's Nyquist limit. Use a compatible correction or a higher output sample rate.");
        return format.SampleRate;
    }
    internal static int ReadChannelMask(WaveFormat format)
    {
        if(format is not WaveFormatExtensible)return 0;
        var pointer=WaveFormat.MarshalToPtr(format);try{return Marshal.ReadInt32(pointer,20);}finally{Marshal.FreeHGlobal(pointer);}
    }
    public void Start(RouteEndpoint input,RouteEndpoint physical,int expectedChannels,int latencyMs,double volume)
    {
        if(!input.Virtual||physical.Virtual)throw new InvalidOperationException("Choose a virtual cable as input and physical headphones as output. Sending audio back into a virtual cable would create a feedback loop.");
        if(input.Channels<2||input.Channels>32||expectedChannels==2&&input.Channels!=2)throw new InvalidOperationException("Choose a stereo cable for the stereo relay, or apply a verified spatial engine before using a multichannel capture device.");
        if(input.ChannelMask!=0&&(input.ChannelMask&3)!=3)throw new InvalidOperationException("The selected virtual capture layout does not begin with the front left/right binaural pair.");
        if(physical.Channels<2)throw new InvalidOperationException("Choose a stereo headphone or speaker output.");
        if(latencyMs<20||latencyMs>200||!double.IsFinite(volume)||volume<0||volume>1)throw new InvalidDataException("Invalid relay buffer or volume.");
        Stop();
        try
        {
            enumerator=new MMDeviceEnumerator();inputDevice=enumerator.GetDevice(input.Id);outputDevice=enumerator.GetDevice(physical.Id);
            if(inputDevice.DataFlow!=DataFlow.Capture||outputDevice.DataFlow!=DataFlow.Render||!IsVirtualCable(inputDevice.FriendlyName,inputDevice.DeviceFriendlyName)||IsVirtualCable(outputDevice.FriendlyName,outputDevice.DeviceFriendlyName))throw new InvalidOperationException("The selected route no longer matches the virtual-input/physical-output selection. Refresh devices.");
            capture=new WasapiCapture(inputDevice,true,20);var format=capture.WaveFormat;
            if(format.Channels<2||expectedChannels==2&&format.Channels!=2)throw new InvalidOperationException("The capture format changed. Refresh and select the correct route again.");
            var standard=format is WaveFormatExtensible ext?ext.ToStandardWaveFormat():format;
            buffer=new BufferedWaveProvider(standard){BufferDuration=TimeSpan.FromMilliseconds(500),DiscardOnBufferOverflow=true,ReadFully=true};
            Stereo=new StereoRelayProvider(buffer.ToSampleProvider()){Volume=(float)volume};
            ISampleProvider provider=Stereo;using var outputClient=outputDevice.AudioClient;var outputFormat=outputClient.MixFormat;int outputRate=outputFormat.SampleRate;
            if(outputFormat.Channels<2)throw new InvalidOperationException("The headphone output no longer reports a stereo format. Refresh devices.");
            if(provider.WaveFormat.SampleRate!=outputRate)provider=new WdlResamplingSampleProvider(provider,outputRate);
            output=new WasapiOut(outputDevice,AudioClientShareMode.Shared,true,latencyMs);output.Init(provider);
            int token=Generation;
            capture.DataAvailable+=(_,args)=>{
                if(token!=Generation||!running)return;
                var active=buffer;if(active is null)return;
                try
                {
                    if(active.BufferedDuration.TotalMilliseconds>Math.Min(400,Math.Max(120,latencyMs*3))||active.BufferLength-active.BufferedBytes<args.BytesRecorded){active.ClearBuffer();DropCount++;}
                    active.AddSamples(args.Buffer,0,args.BytesRecorded);
                }
                catch(Exception ex){ReportFault(token,"Virtual-input capture failed: "+ex.Message);}
            };
            capture.RecordingStopped+=(_,args)=>{if(args.Exception is not null)ReportFault(token,"Audio input stopped: "+args.Exception.Message);};
            output.PlaybackStopped+=(_,args)=>{if(args.Exception is not null)ReportFault(token,"Headphone playback stopped: "+args.Exception.Message);};
            DropCount=0;BufferMilliseconds=latencyMs;running=true;capture.StartRecording();output.Play();
        }
        catch{Stop();throw;}
    }
    void ReportFault(int token,string message){if(token!=Generation||!running)return;running=false;Faulted?.Invoke(token,message);}
    public void Stop()
    {
        Interlocked.Increment(ref generation);running=false;
        var oldCapture=capture;var oldOutput=output;var oldInput=inputDevice;var oldHeadphones=outputDevice;var oldEnumerator=enumerator;
        capture=null;output=null;inputDevice=null;outputDevice=null;enumerator=null;buffer=null;Stereo=null;
        var errors=new List<Exception>();void Attempt(Action action){try{action();}catch(Exception ex){errors.Add(ex);}}
        if(oldCapture is not null)Attempt(oldCapture.StopRecording);if(oldOutput is not null)Attempt(oldOutput.Stop);
        foreach(var item in new IDisposable?[]{oldCapture,oldOutput,oldInput,oldHeadphones,oldEnumerator})if(item is not null)Attempt(item.Dispose);
        if(errors.Count>0)throw new AggregateException("Some audio handles did not close cleanly. Reopen the app if the output remains busy.",errors);
    }
    public void Dispose()=>Stop();
}

/// <summary>Reads only the binaural L/R pair produced by the upstream spatial engine.</summary>
public sealed class StereoRelayProvider : ISampleProvider
{
    readonly ISampleProvider source;
    float[] scratch=Array.Empty<float>();
    readonly int channels;
    volatile float volume=0.8f;
    volatile float peak;
    public WaveFormat WaveFormat {get;}
    public float Peak=>peak;
    public float Volume{get=>volume;set{if(!float.IsFinite(value)||value<0||value>1)throw new ArgumentOutOfRangeException(nameof(value));volume=value;}}
    public StereoRelayProvider(ISampleProvider source)
    {
        this.source=source;channels=source.WaveFormat.Channels;if(channels<2)throw new ArgumentException("The relay needs at least two input channels.");WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate,2);
    }
    public int Read(float[] destination,int offset,int count)
    {
        if(count%2!=0)count--;int frames=count/2;int needed=frames*channels;
        if(scratch.Length<needed)scratch=new float[needed];int read=source.Read(scratch,0,needed);int frameCount=read/channels;float multiplier=volume,max=0;
        for(int f=0;f<frameCount;f++)for(int c=0;c<2;c++){float sample=scratch[f*channels+c];sample=float.IsFinite(sample)?Math.Clamp(sample*multiplier,-1,1):0;destination[offset+f*2+c]=sample;max=Math.Max(max,Math.Abs(sample));}
        peak=max;return frameCount*2;
    }
}
