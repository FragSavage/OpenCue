using System.Globalization;
using System.Text;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace OpenCue;
internal static class SelfTests
{
    public static int Run(string reportPath)
    {
        var results=new List<string>();int passed=0,failed=0;
        string scratch=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!,"opencue-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        void Check(string name,Action test){try{test();passed++;results.Add("PASS "+name);}catch(Exception ex){failed++;results.Add("FAIL "+name+": "+ex);}}
        void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        void Reject(Action action){bool rejected=false;try{action();}catch{rejected=true;}Assert(rejected,"Invalid operation was accepted.");}
        EngineLocation Prepare(EngineLocation engine)
        {
            var header=new byte[256];header[0]=0x4d;header[1]=0x5a;header[0x3c]=0x80;header[0x80]=0x50;header[0x81]=0x45;header[0x84]=0x64;header[0x85]=0x86;File.WriteAllBytes(Path.Combine(engine.InstallPath,"EqualizerAPO.dll"),header);return engine;
        }
        var endpoint=new RenderEndpoint("{0.0.0.00000000}.{01234567-89ab-cdef-0123-456789abcdef}","Test headphones");
        var plugin=Path.Combine(scratch,"path with spaces","OpenCueDynamics.dll");
        Check("Endpoint GUID scope never depends on a friendly-name substring",()=>{var text=EngineService.Compile(new AudioProfile(),endpoint,plugin);Assert(text.Contains("Device: {01234567-89ab-cdef-0123-456789abcdef}"),"Wrong device selector");Assert(!text.Contains("Test headphones"),"Friendly name used for routing");});
        Check("Compile uses ten filters, front stereo scope and independent peak protection",()=>{var text=EngineService.Compile(AudioProfile.Presets[1],endpoint,plugin);Assert(text.Split("Filter ").Length-1==10,"Filter count");Assert(text.Contains("Channel: L R"),"Stereo channel command missing");Assert(text.Contains("CompOn 0")&&text.Contains("LimitOn 1"),"Compression and limiter controls are not independent");});
        Check("Dynamics parameter units match native plugin ABI",()=>{var p=new AudioProfile{DynamicsEnabled=true,ThresholdDb=-15,Ratio=4.5,CeilingDb=-6.25};var text=EngineService.Compile(p,endpoint,plugin);Assert(text.Contains("Threshold 0.5 Ratio 0.5 Ceiling 0.5"),"Normalized parameter mapping differs");Assert(text.IndexOf("VSTPlugin:")>text.LastIndexOf("Filter "),"Limiter not after EQ");});
        Check("Every fine control maps to its native parameter",()=>{var p=new AudioProfile{DynamicsEnabled=true,AttackMs=50,ReleaseMs=500,MakeupDb=12,TransientAmount=0.75,AmbienceAmount=0.4,FloorDb=-65,LevelingAmount=0.8,Width=2,Balance=-1};var text=EngineService.Compile(p,endpoint,plugin);foreach(var expected in new[]{"CompOn 1","Attack 1","Release 1","Makeup 1","Transient 0.75","Ambience 0.4","Floor 0.25","Leveling 0.8","Width 1","Balance 0","LimitOn 1"})Assert(text.Contains(expected),"Missing or incorrect mapping: "+expected);});
        Check("7.1 front-transient processing precedes measured rendering and tone",()=>{var p=new AudioProfile{SpatialEnabled=true,TransientAmount=0.75};var spatial=Path.Combine(scratch,"OpenCueSpatial.dll");var text=EngineService.Compile(p,endpoint,plugin,null,spatial);int render=text.IndexOf(spatial,StringComparison.Ordinal);Assert(text.IndexOf("Transient 0.75",StringComparison.Ordinal)<render,"Front transient pass not before spatial rendering");Assert(render<text.IndexOf("Filter 1:",StringComparison.Ordinal),"Spatial renderer not before stereo tone");Assert(text.LastIndexOf("Transient 0 ",StringComparison.Ordinal)>render,"Transient applied twice to binaural output");Assert(text.Contains("Channel: L R C LFE RL RR SL SR"),"7.1 ordering wrong");});
        Check("All-output mode targets only explicit render GUIDs, never Device all",()=>{var other=new RenderEndpoint("{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}","Second playback output");var p=new AudioProfile{AllDevices=true};Reject(()=>EngineService.Compile(p,null,plugin));var text=EngineService.Compile(p,null,plugin,[endpoint,other]);Assert(text.Contains("Device: "+endpoint.GuidSelector+"; "+other.GuidSelector),"Playback scopes missing");Assert(text.IndexOf("Device: all")>text.IndexOf("Filter 10:"),"Unrestricted device selector precedes effects");});
        Check("Decimal serialization independent of Windows culture",()=>{var old=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=new CultureInfo("fr-FR");var p=new AudioProfile{DynamicsEnabled=true,PreampDb=-6.5};var text=EngineService.Compile(p,endpoint,plugin);Assert(text.Contains("-6.5 dB") && text.Contains("31.5 Hz"),"Comma decimals emitted");}finally{CultureInfo.CurrentCulture=old;}});
        Check("Bypass compiles to comments only, without a device requirement",()=>{var text=EngineService.Compile(new AudioProfile{Enabled=false},null,plugin);Assert(!text.Contains("Preamp:")&&!text.Contains("Filter ")&&!text.Contains("VSTPlugin:"),"Bypass still processes");});
        Check("Unknown devices and unsafe numeric inputs rejected",()=>{Reject(()=>EngineService.Compile(new AudioProfile(),null,plugin));Reject(()=>new AudioProfile{Gains=[double.NaN,0,0,0,0,0,0,0,0,0]}.Validate());Reject(()=>new AudioProfile{Gains=new double[9]}.Validate());Reject(()=>new AudioProfile{Ratio=double.PositiveInfinity}.Validate());Reject(()=>new AudioProfile{Version=99}.Validate());Reject(()=>new RenderEndpoint("fake endpoint","fake").GuidSelector.ToString());});
        Check("Headroom check blocks unsafe boosts",()=>{var p=new AudioProfile{PreampDb=0,Gains=[0,0,0,0,0,3,3,3,0,0]};Reject(()=>EngineService.Compile(p,endpoint,plugin));p.PreampDb=p.SafePreamp;Assert(p.PreampDb==-10,"Conservative headroom total incorrect");EngineService.Compile(p,endpoint,plugin);});
        int index=0;
        foreach(var encoding in new Encoding[]{new UTF8Encoding(false),new UTF8Encoding(true)})
        foreach(string ending in new[]{"","\n","\r\n"})
        {
            var copyEncoding=encoding;string copyEnding=ending;
            Check($"Apply/bypass/restore byte preservation {encoding.WebName}, BOM {encoding.GetPreamble().Length}, ending {ending.Length}",()=>
            {
                var folder=Path.Combine(scratch,"configuration ö "+index++);Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));
                string originalText="# Existing café settings\nPreamp: -2 dB"+copyEnding;byte[] original=copyEncoding.GetPreamble().Concat(copyEncoding.GetBytes(originalText)).ToArray();File.WriteAllBytes(engine.RootFile,original);
                EngineService.Apply(engine,new AudioProfile(),endpoint);EngineService.Apply(engine,AudioProfile.Presets[1],endpoint);
                Assert(File.ReadAllBytes(Path.Combine(engine.OwnDirectory,"config.before-OpenCue.bin")).SequenceEqual(original),"Original backup not byte exact");
                var root=EngineService.Decode(File.ReadAllBytes(engine.RootFile));Assert(root.Text.Split(EngineService.Begin).Length-1==1,"Repeated Apply duplicates include");
                Assert(File.ReadAllText(engine.ActiveFile).Contains("Gain 2 dB"),"Preset did not update");EngineService.Bypass(engine);Assert(!File.ReadAllText(engine.ActiveFile).Contains("Filter "),"Bypass still has EQ");
                EngineService.Restore(engine);Assert(File.ReadAllBytes(engine.RootFile).SequenceEqual(original),"Restore changed preexisting bytes/BOM/newline");
                Assert(File.Exists(Path.Combine(engine.OwnDirectory,"config.before-OpenCue.bin")),"Restore removed backup");
            });
        }
        Check("UTF-16 input is rejected without a misleading applied state",()=>
        {
            foreach(var encoding in new Encoding[]{new UnicodeEncoding(false,true),new UnicodeEncoding(true,true)})
            {
                var folder=Path.Combine(scratch,"unsupported encoding "+index++);Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));var original=encoding.GetPreamble().Concat(encoding.GetBytes("# Existing config\r\n")).ToArray();File.WriteAllBytes(engine.RootFile,original);Reject(()=>EngineService.Apply(engine,new AudioProfile(),endpoint));Assert(File.ReadAllBytes(engine.RootFile).SequenceEqual(original),"Unsupported root changed");
            }
        });
        Check("External edits survive surgical integration removal",()=>
        {
            var folder=Path.Combine(scratch,"external edits");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));File.WriteAllText(engine.RootFile,"# Original\n");EngineService.Apply(engine,new AudioProfile(),endpoint);
            string text=File.ReadAllText(engine.RootFile);File.WriteAllText(engine.RootFile,"# Added before\n"+text+"# Added after\n");EngineService.Restore(engine);Assert(File.ReadAllText(engine.RootFile)=="# Added before\n# Original\n# Added after\n","External root changes lost");
        });
        Check("Damaged markers leave root bytes untouched",()=>
        {
            var folder=Path.Combine(scratch,"damaged");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));File.WriteAllText(engine.RootFile,"# Original\n"+EngineService.Begin+"\n");var before=File.ReadAllBytes(engine.RootFile);Reject(()=>EngineService.Apply(engine,new AudioProfile(),endpoint));Reject(()=>EngineService.Restore(engine));Assert(File.ReadAllBytes(engine.RootFile).SequenceEqual(before),"Damaged config overwritten");
        });
        Check("Edited managed content is preserved and refused",()=>
        {
            var folder=Path.Combine(scratch,"edited block");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));File.WriteAllText(engine.RootFile,"# Original\n");EngineService.Apply(engine,new AudioProfile(),endpoint);File.WriteAllText(engine.RootFile,File.ReadAllText(engine.RootFile).Replace("Include: OpenCue\\active.txt","Preamp: 6 dB\nInclude: OpenCue\\active.txt"));var before=File.ReadAllBytes(engine.RootFile);Reject(()=>EngineService.Apply(engine,new AudioProfile(),endpoint));Reject(()=>EngineService.Restore(engine));Assert(File.ReadAllBytes(engine.RootFile).SequenceEqual(before),"Edited block discarded");
        });
        Check("Canonical nested duplicate Include is rejected before mutation",()=>
        {
            var folder=Path.Combine(scratch,"nested includes");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));File.WriteAllText(engine.RootFile,"Include: secondary.txt\n");File.WriteAllText(Path.Combine(folder,"secondary.txt"),"Include: \""+engine.ActiveFile.Replace('\\','/')+"\"\n");Reject(()=>EngineService.Apply(engine,new AudioProfile(),endpoint));Assert(File.ReadAllText(engine.RootFile)=="Include: secondary.txt\n","Root changed on duplicate reference");
        });
        Check("Later external effects are not silently placed after limiter",()=>
        {
            var folder=Path.Combine(scratch,"later effects");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));File.WriteAllText(engine.RootFile,"# Original\n");EngineService.Apply(engine,new AudioProfile(),endpoint);File.AppendAllText(engine.RootFile,"Preamp: 9 dB\n");Reject(()=>EngineService.Apply(engine,new AudioProfile(),endpoint));EngineService.Restore(engine);Assert(File.ReadAllText(engine.RootFile).Contains("Preamp: 9 dB"),"Later effects lost during recovery");
        });
        Check("Unknown active filename is not overwritten",()=>
        {
            var folder=Path.Combine(scratch,"foreign active");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));File.WriteAllText(engine.RootFile,"# Original\n");Directory.CreateDirectory(engine.OwnDirectory);File.WriteAllText(engine.ActiveFile,"someone else's settings");Reject(()=>EngineService.Apply(engine,new AudioProfile(),endpoint));Assert(File.ReadAllText(engine.ActiveFile)=="someone else's settings","Unknown file overwritten");Assert(File.ReadAllText(engine.RootFile)=="# Original\n","Root changed before ownership refusal");
        });
        Check("Conditional root requires manual review without mutation",()=>
        {
            var folder=Path.Combine(scratch,"conditional");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));string content="If: inputChannelCount == 2\nPreamp: -6 dB\nEndIf:\n";File.WriteAllText(engine.RootFile,content);Reject(()=>EngineService.Apply(engine,new AudioProfile(),endpoint));Assert(File.ReadAllText(engine.RootFile)==content,"Conditional config changed");
        });
        Check("Original dynamics resource can be applied in a scratch engine",()=>
        {
            var folder=Path.Combine(scratch,"native plugin");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));File.WriteAllText(engine.RootFile,"# Scratch only\n");var header=new byte[256];header[0]=0x4d;header[1]=0x5a;header[0x3c]=0x80;header[0x80]=0x50;header[0x81]=0x45;header[0x84]=0x64;header[0x85]=0x86;File.WriteAllBytes(Path.Combine(folder,"EqualizerAPO.dll"),header);EngineService.Apply(engine,new AudioProfile{DynamicsEnabled=true},endpoint);var pluginPath=Path.Combine(engine.OwnDirectory,"OpenCueDynamics-v2.dll");Assert(File.Exists(pluginPath)&&new FileInfo(pluginPath).Length>10000,"Embedded plugin not deployed");Assert(File.ReadAllText(engine.ActiveFile).Contains("VSTPlugin: Library \""+pluginPath+"\""),"Plugin absolute path wrong");EngineService.Restore(engine);
        });
        Check("Wrong dynamics architecture and stage roots are rejected",()=>
        {
            var folder=Path.Combine(scratch,"incompatible engine");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));File.WriteAllText(engine.RootFile,"# Original\n");File.Delete(Path.Combine(folder,"EqualizerAPO.dll"));Reject(()=>EngineService.Apply(engine,new AudioProfile{DynamicsEnabled=true},endpoint));Assert(File.ReadAllText(engine.RootFile)=="# Original\n","Root changed on architecture refusal");File.WriteAllText(engine.RootFile,"Stage: pre-mix post-mix\n");Reject(()=>EngineService.Apply(engine,new AudioProfile(),endpoint));
        });
        Check("Windows endpoint enumeration reads active playback outputs",()=>{var outputs=EndpointService.Enumerate();Assert(outputs.Select(o=>o.Id).Distinct().Count()==outputs.Count,"Duplicate endpoints");foreach(var item in outputs){Assert(!string.IsNullOrWhiteSpace(item.Name),"Empty output name");_ =item.GuidSelector;}results.Add("INFO Active render endpoints: "+outputs.Count);});
        Check("Virtual relay preserves the rendered L/R pair and applies actual volume",()=>{var source=new TestSamples(8,[0.5f,-0.25f,10,11,12,13,14,15,float.NaN,2,20,21,22,23,24,25]);var stereo=new StereoRelayProvider(source){Volume=0.5f};var output=new float[8];int read=stereo.Read(output,2,4);Assert(read==4,"Stereo frame count incorrect");Assert(output[2]==0.25f&&output[3]==-0.125f&&output[4]==0&&output[5]==1,"Wrong channels, volume or finite bounds");Assert(output[0]==0&&output[6]==0,"Read overwrote outside requested destination");Assert(stereo.Peak==1,"Actual output meter not updated");});
        Check("Relay rejects physical capture and virtual feedback before device activation",()=>{using var r=new RelayService();var input=new RouteEndpoint("fake","Microphone",2,48000,3,false);var destination=new RouteEndpoint("fake2","Virtual cable",2,48000,3,true);Reject(()=>r.Start(input,destination,2,60,0.8));});
        Check("Native seven-channel-mask structure is read at the Windows ABI offset",()=>{var format=new WaveFormatExtensible(48000,32,8);var pointer=WaveFormat.MarshalToPtr(format);try{Marshal.WriteInt32(pointer,20,0x63f);var copy=WaveFormat.MarshalFromPtr(pointer);Assert(RelayService.ReadChannelMask(copy)==0x63f,"Channel mask was lost or read at a wrong offset");}finally{Marshal.FreeHGlobal(pointer);}});
        Check("Official 16ch local profile remains opaque and exact",()=>{var folder=Path.Combine(scratch,"official art local");Directory.CreateDirectory(folder);var engine=Prepare(new EngineLocation(folder,folder));var library=Path.Combine(folder,"atk_spatial_engine_bravo_v2_0_0.dll");File.Copy(Path.Combine(folder,"EqualizerAPO.dll"),library);var path=Path.Combine(folder,"BO7_V5_16ch_Test.txt");var content="# Synthetic syntax fixture, no real engine executed\nVSTPlugin: Library \""+library+"\" ChunkData \"AA==\"\n";File.WriteAllText(path,content);var parsed=ArtProfileService.Validate(path);Assert(parsed.LibraryPath==library&&File.ReadAllText(path)==content,"Profile was modified");var text=EngineService.Compile(new AudioProfile{Art16ProfilePath=path},endpoint,plugin);Assert(text.Contains("Include: "+path),"Exact local profile reference missing");File.AppendAllText(path,"Preamp: 6 dB\n");Reject(()=>ArtProfileService.Validate(path));});
        Check("Imported correction is real numeric EQ before tone and dynamics",()=>{var parsed=CorrectionService.Parse("Preamp: -3 dB\nGraphicEQ: 20 -2; 1000 2; 16000 -1");var p=new AudioProfile{HeadphoneCorrection=parsed.NormalizedText,CorrectionName="Test correction",PreampDb=-6};var text=EngineService.Compile(p,endpoint,plugin);Assert(text.IndexOf("GraphicEQ:",StringComparison.Ordinal)<text.IndexOf("Filter 1:",StringComparison.Ordinal),"Correction not before tone");Assert(p.SafePreamp==-3,"Correction gain not included in headroom");Reject(()=>CorrectionService.Parse("Include: unknown.txt"));});
        Check("Explicit controller takeover preserves and recovers prior routing",()=>EngineTakeoverService.SelfTest(scratch));
        results.Insert(0,$"OpenCue integration verification: {passed} passed, {failed} failed. {DateTimeOffset.UtcNow:O}");results.Add("Only scratch files and read-only endpoint enumeration were used. No live audio configuration was modified.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);File.WriteAllLines(reportPath,results);return failed==0?0:1;
    }
    sealed class TestSamples(int channels,float[] data):ISampleProvider
    {
        int position;
        public WaveFormat WaveFormat {get;}=WaveFormat.CreateIeeeFloatWaveFormat(48000,channels);
        public int Read(float[] output,int offset,int count){int size=Math.Min(count,data.Length-position);Array.Copy(data,position,output,offset,size);position+=size;return size;}
    }
}
