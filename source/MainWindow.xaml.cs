using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OpenCue;
public partial class MainWindow : Window
{
    readonly Slider[] bands = new Slider[10];
    readonly TextBlock[] bandValues = new TextBlock[10];
    readonly AudioProfile[] presets = AudioProfile.Presets;
    AudioProfile profile = new();
    EngineLocation? engine;
    bool loading = true;
    string? leqPath;
    readonly DispatcherTimer liveTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    readonly DispatcherTimer relayTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    readonly RelayService relay=new();
    string routeLabel="";
    bool lastApplySucceeded;
    readonly string stateFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenCue", "last-profile.json");
    public MainWindow()
    {
        InitializeComponent();
        for (int i = 0; i < 10; i++)
        {
            int index = i;
            var panel = new StackPanel { Width = 83, HorizontalAlignment = HorizontalAlignment.Center };
            bandValues[i] = new TextBlock { Text="0.0", FontFamily = new FontFamily("Consolas"), FontSize=12, HorizontalAlignment=HorizontalAlignment.Center, Foreground=(Brush)FindResource("AccentBrush"), Margin=new Thickness(0,0,0,9) };
            bands[i] = new Slider { Style=(Style)FindResource("EqSlider"), Minimum=-12, Maximum=6, Value=0, Orientation=Orientation.Vertical, Width=28, Height=126, TickFrequency=0.5, IsSnapToTickEnabled=true };
            System.Windows.Automation.AutomationProperties.SetName(bands[i], $"{AudioProfile.Labels[i]} hertz gain in decibels");
            bands[i].ValueChanged += (_,_) => { bandValues[index].Text=bands[index].Value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture); Changed(); };
            panel.Children.Add(bandValues[i]); panel.Children.Add(bands[i]);
            panel.Children.Add(new TextBlock { Text=AudioProfile.Labels[i], FontSize=11, HorizontalAlignment=HorizontalAlignment.Center, Margin=new Thickness(0,9,0,0), Foreground=(Brush)FindResource("MutedBrush") });
            BandPanel.Children.Add(panel);
        }
        PresetBox.ItemsSource = presets.Select(p=>p.Name).Append("Custom").ToArray();
        PresetBox.SelectionChanged += (_,_) => { if (!loading && PresetBox.SelectedIndex >= 0 && PresetBox.SelectedIndex<presets.Length) { var id=profile.DeviceId; bool all=profile.AllDevices,spatial=profile.SpatialEnabled;var art=profile.Art16ProfilePath;var correction=profile.HeadphoneCorrection;var correctionName=profile.CorrectionName; profile=presets[PresetBox.SelectedIndex].Copy(); profile.DeviceId=id; profile.AllDevices=all;profile.SpatialEnabled=spatial;profile.Art16ProfilePath=art;profile.HeadphoneCorrection=correction;profile.CorrectionName=correctionName; LoadControls(); Changed(false); } };
        DeviceBox.SelectionChanged += (_,_) => { if (!loading && DeviceBox.SelectedItem is RenderEndpoint selected) { profile.DeviceId=selected.Id; Changed(false); } };
        HeadroomSlider.ValueChanged += (_,_) => { PreampValue.Text=HeadroomSlider.Value.ToString("0.0", CultureInfo.CurrentCulture)+" dB"; Changed(); };
        foreach (var slider in new[] {ThresholdSlider,RatioSlider,CeilingSlider,AttackSlider,ReleaseSlider,MakeupSlider,TransientSlider,AmbienceSlider,FloorSlider,LevelingSlider,WidthSlider,BalanceSlider}) slider.ValueChanged += (_,_)=>Changed();
        foreach (var check in new[] { EnableToggle, DynamicsToggle, ScopeAllToggle,LimiterToggle }) { check.Checked += (_,_)=>Changed(); check.Unchecked += (_,_)=>Changed(); }
        liveTimer.Tick+=(_,_)=>{liveTimer.Stop();if(LiveToggle.IsChecked==true)RunAction(ApplySound);};
        LiveToggle.Checked+=(_,_)=>{if(!loading)Changed();};LiveToggle.Unchecked+=(_,_)=>liveTimer.Stop();
        NaturalModeButton.Click+=(_,_)=>SetMode(0);CompressedModeButton.Click+=(_,_)=>SetMode(1);BoostModeButton.Click+=(_,_)=>SetMode(2);
        CurveCanvas.SizeChanged += (_,_)=>DrawCurve();
        ApplyButton.Click += (_,_)=>RunAction(ApplySound);
        BypassButton.Click += (_,_)=>RunAction(BypassSound);
        RestoreButton.Click += (_,_)=>RunAction(()=>{ RequireEngine();relay.Stop();RefreshRelayUi(); EngineService.Restore(engine!); StatusText.Text="OpenCue integration removed. Other configuration edits and the original backup were kept. Select your headphones as the game output for normal playback."; RefreshStatus(); });
        ExportButton.Click += (_,_)=>RunAction(Export);
        ImportButton.Click += (_,_)=>RunAction(Import);
        ImportCorrectionButton.Click+=(_,_)=>RunAction(ImportCorrection);
        ClearCorrectionButton.Click+=(_,_)=>{profile.HeadphoneCorrection=null;profile.CorrectionName=null;LoadControls();Changed();};
        RefreshButton.Click += (_,_)=>RunAction(()=>{ ReadControls(); RefreshDevices(); RefreshStatus(); if(DeviceBox.SelectedItem is not null) StatusText.Text="Output devices refreshed. Match the selected device in Device Selector."; });
        GetEngineButton.Click += async (_,_)=>await StartSetup();
        SelectorButton.Click += (_,_)=>RunAction(OpenSelector);
        OpenConfigButton.Click += (_,_)=>RunAction(()=>{RequireEngine(); Process.Start(new ProcessStartInfo("notepad.exe", engine!.RootFile) {UseShellExecute=true});});
        LeqButton.Click += async (_,_)=>await OpenLeq();
        LocateLeqButton.Click += async (_,_)=>{var dialog=new OpenFileDialog{Title="Choose the official LEQ Control Panel",Filter="LEQ Control Panel (*.exe)|*.exe"};if(dialog.ShowDialog(this)==true){leqPath=dialog.FileName;await OpenLeq();}};
        RelayModeBox.ItemsSource=new[]{"Stereo cable","OpenCue 7.1","Official ArtTune 16ch"};RelayModeBox.SelectedIndex=0;
        RelayRefreshButton.Click+=(_,_)=>RunAction(RefreshRoutes);
        RelayStartButton.Click+=(_,_)=>RunAction(StartRelay);
        RelayStopButton.Click+=(_,_)=>RunAction(()=>{relay.Stop();RelayStatusText.Text="Game audio relay stopped. Choose your physical headphones in the game to use its normal output.";RefreshRelayUi();});
        RelayVolumeSlider.ValueChanged+=(_,_)=>{if(relay.Stereo is not null)relay.Stereo.Volume=(float)RelayVolumeSlider.Value;};
        SpatialApplyButton.Click+=(_,_)=>RunAction(ApplySpatial);
        UseStereoButton.Click+=(_,_)=>RunAction(ReturnToStereo);
        SpatialToggle.Checked+=(_,_)=>{if(!loading){profile.Art16ProfilePath=null;Changed();UpdateSpatialInfo();}};
        SpatialToggle.Unchecked+=(_,_)=>{if(!loading){Changed();UpdateSpatialInfo();}};
        CableButton.Click+=async(_,_)=>await SetupCable();
        SoundSettingsButton.Click+=(_,_)=>RunAction(()=>Process.Start(new ProcessStartInfo("control.exe","mmsys.cpl"){UseShellExecute=true}));
        ArtSetupButton.Click+=(_,_)=>SetupService.OpenUrl("https://github.com/ArtIsWar/ArtTuneDB");
        LoadArtProfileButton.Click+=(_,_)=>RunAction(LoadArtProfile);
        relay.Faulted+=(token,message)=>Dispatcher.BeginInvoke(()=>{if(relay.Generation!=token)return;try{relay.Stop();}catch{}RelayStatusText.Text=message+" Reconnect the selected devices and start again.";RefreshRelayUi();});
        relayTimer.Tick+=(_,_)=>{RelayMeter.Value=relay.Stereo?.Peak??0;if(relay.Running)RelayStatusText.Text=$"Running · {routeLabel}\nBuffer target {relay.BufferMilliseconds} ms · Queued {relay.BufferedMilliseconds:0} ms · Buffer recoveries {relay.DropCount}. Keep OpenCue open.";};relayTimer.Start();
        TestLeftButton.Click += async (_,_)=>await PlayChannel(false);
        TestRightButton.Click += async (_,_)=>await PlayChannel(true);
        if (File.Exists(stateFile))
        {
            try { var stored=JsonSerializer.Deserialize<AudioProfile>(File.ReadAllText(stateFile)) ?? throw new InvalidDataException("Saved profile is empty."); stored.Validate(); profile=stored; }
            catch(Exception ex) { StatusText.Text="The last preset could not be loaded: "+ex.Message; }
        }
        try {RefreshDevices();} catch(Exception ex){StatusText.Text="Output detection failed: "+ex.Message+" Select Refresh to try again.";}
        LoadControls(); loading=false; RefreshStatus();
        leqPath=LeqService.Find();LeqStatusText.Text=leqPath is null ? "Optional tool not found. Choose the supplied portable .exe." : "Portable LEQ file found · Its official v1.0.0 hash is checked when opened.";
        try{RefreshRoutes();}catch(Exception ex){RelayStatusText.Text="Routing discovery failed: "+ex.Message;}
        if(engine is null) MainTabs.SelectedIndex=1;
        Loaded += (_,_)=>DrawCurve();
        Closing += (_,_)=>{liveTimer.Stop();relayTimer.Stop();try{relay.Dispose();}catch{}try {ReadControls(); SaveState();} catch {} };
    }
    void RunAction(Action action) { try { action(); } catch(Exception ex) { StatusText.Text=ex.Message; SetupProgressText.Text=ex.Message;RelayStatusText.Text=ex.Message;SpatialSetupText.Text=ex.Message; } }
    void RequireEngine()
    {
        engine=EngineService.Detect(); if(engine is null) {MainTabs.SelectedIndex=1; throw new InvalidOperationException("The free audio engine is not installed. Select Start setup, then follow the three steps.");}
    }
    void RefreshDevices()
    {
        var devices=EndpointService.Enumerate(); bool previous=loading; loading=true;
        DeviceBox.ItemsSource=devices;
        DeviceBox.SelectedItem=devices.FirstOrDefault(d=>d.Id==profile.DeviceId) ?? (profile.DeviceId is null ? devices.FirstOrDefault(d=>d.IsDefault) ?? devices.FirstOrDefault() : null);
        loading=previous;
        if(DeviceBox.SelectedItem is RenderEndpoint selected) profile.DeviceId=selected.Id;
        if(devices.Count==0) StatusText.Text="No active playback output was found. Connect your headphones, then select Refresh.";
        else if(DeviceBox.SelectedItem is null) StatusText.Text="Your saved output is disconnected. Select another output explicitly, or reconnect it.";
    }
    void LoadControls()
    {
        bool previous=loading; loading=true;
        int presetIndex=Array.FindIndex(presets,p=>p.Name==profile.Name);PresetBox.SelectedIndex=presetIndex<0?presets.Length:presetIndex;
        for(int i=0;i<10;i++){bands[i].Value=profile.Gains[i];bandValues[i].Text=profile.Gains[i].ToString("+0.0;-0.0;0.0");}
        HeadroomSlider.Value=profile.PreampDb; PreampValue.Text=profile.PreampDb.ToString("0.0")+" dB";
        EnableToggle.IsChecked=profile.Enabled; ScopeAllToggle.IsChecked=profile.AllDevices;
        DynamicsToggle.IsChecked=profile.DynamicsEnabled; ThresholdSlider.Value=profile.ThresholdDb; RatioSlider.Value=profile.Ratio; CeilingSlider.Value=profile.CeilingDb;
        AttackSlider.Value=profile.AttackMs;ReleaseSlider.Value=profile.ReleaseMs;MakeupSlider.Value=profile.MakeupDb;TransientSlider.Value=profile.TransientAmount;AmbienceSlider.Value=profile.AmbienceAmount;FloorSlider.Value=profile.FloorDb;LevelingSlider.Value=profile.LevelingAmount;WidthSlider.Value=profile.Width;BalanceSlider.Value=profile.Balance;LimiterToggle.IsChecked=profile.LimiterEnabled;
        SpatialToggle.IsChecked=profile.SpatialEnabled;UpdateSpatialInfo();
        CorrectionStatusText.Text=profile.Correction is CorrectionData correction?$"{profile.CorrectionName??"Imported correction"} · {correction.FilterCount} EQ commands. Applied before your tone sliders.":"Optional: import a compatible headphone EQ text file.";
        PresetDescription.Text=AudioProfile.Description(profile.Name); UpdateDynamicsText(); loading=previous; DrawCurve();
    }
    void ReadControls()
    {
        profile.Gains=bands.Select(b=>b.Value).ToArray(); profile.PreampDb=HeadroomSlider.Value;
        profile.Enabled=EnableToggle.IsChecked==true; profile.AllDevices=ScopeAllToggle.IsChecked==true;
        profile.DynamicsEnabled=DynamicsToggle.IsChecked==true; profile.ThresholdDb=ThresholdSlider.Value; profile.Ratio=RatioSlider.Value; profile.CeilingDb=CeilingSlider.Value;
        profile.AttackMs=AttackSlider.Value;profile.ReleaseMs=ReleaseSlider.Value;profile.MakeupDb=MakeupSlider.Value;profile.TransientAmount=TransientSlider.Value;profile.AmbienceAmount=AmbienceSlider.Value;profile.FloorDb=FloorSlider.Value;profile.LevelingAmount=LevelingSlider.Value;profile.Width=WidthSlider.Value;profile.Balance=BalanceSlider.Value;profile.LimiterEnabled=LimiterToggle.IsChecked==true;
        profile.SpatialEnabled=SpatialToggle.IsChecked==true;
        if(DeviceBox.SelectedItem is RenderEndpoint selected)profile.DeviceId=selected.Id;
    }
    void Changed(bool markCustom=true)
    {
        if(loading)return; ReadControls(); UpdateDynamicsText(); DrawCurve();
        if(markCustom){profile.Name="Custom";loading=true;PresetBox.SelectedIndex=presets.Length;loading=false;PresetDescription.Text=AudioProfile.Description(profile.Name);}
        StatusText.Text="Changes are ready to apply."+(profile.PreampDb>profile.SafePreamp ? $" Apply will lower preamp to {profile.SafePreamp:0.0} dB to leave room for your boosts." : "");
        liveTimer.Stop();if(LiveToggle.IsChecked==true)liveTimer.Start();
    }
    void UpdateDynamicsText()
    {
        DynamicsText.Text=$"Threshold {ThresholdSlider.Value:0.0} dB   ·   Ratio {RatioSlider.Value:0.0}:1   ·   Ceiling {CeilingSlider.Value:0.0} dBFS";
        AdvancedValuesText.Text=$"Attack {AttackSlider.Value:0} ms  ·  Release {ReleaseSlider.Value:0} ms  ·  Makeup +{MakeupSlider.Value:0.0} dB\nTransient {TransientSlider.Value:P0}  ·  Ambience {AmbienceSlider.Value:P0} below {FloorSlider.Value:0} dBFS  ·  Auto leveling {LevelingSlider.Value:P0}\nStereo width {WidthSlider.Value:0.00}×  ·  Balance {BalanceSlider.Value:0.00}  ·  Peak protection {(LimiterToggle.IsChecked==true?"on":"off")}";
        foreach(var slider in new[]{ThresholdSlider,RatioSlider,AttackSlider,ReleaseSlider}) slider.IsEnabled=DynamicsToggle.IsChecked==true;
        CeilingSlider.IsEnabled=LimiterToggle.IsChecked==true;
    }
    void SetMode(int mode)
    {
        ReadControls();profile.DynamicsEnabled=mode>0;profile.ThresholdDb=mode==2?-20:-16;profile.Ratio=mode==2?4:3;profile.MakeupDb=mode==2?4:0;profile.LevelingAmount=mode==2?0.35:0;profile.TransientAmount=mode==2?0.25:0;profile.LimiterEnabled=true;LoadControls();Changed();
    }
    void BypassSound()
    {
        ReadControls();RequireEngine();
        if(profile.SpatialEnabled||!string.IsNullOrWhiteSpace(profile.Art16ProfilePath))
        {
            var bypass=profile.Copy();bypass.Enabled=true;bypass.Gains=new double[10];bypass.HeadphoneCorrection=null;bypass.CorrectionName=null;bypass.PreampDb=-1;bypass.DynamicsEnabled=false;bypass.MakeupDb=0;bypass.TransientAmount=0;bypass.AmbienceAmount=0;bypass.LevelingAmount=0;bypass.Width=1;bypass.Balance=0;bypass.LimiterEnabled=false;
            EngineService.Apply(engine!,bypass,DeviceBox.SelectedItem as RenderEndpoint);profile.Enabled=true;LoadControls();
            StatusText.Text="Tone and dynamics bypassed. Spatial rendering stays on to preserve the game audio route. Apply sound restores your controls.";
        }
        else{EngineService.Bypass(engine!);profile.Enabled=false;LoadControls();SaveState();StatusText.Text="OpenCue bypassed. Other sound effects remain as configured.";}
        RefreshStatus();
    }
    void UpdateSpatialInfo()
    {
        bool art=!string.IsNullOrWhiteSpace(profile.Art16ProfilePath);ScopeAllToggle.IsEnabled=!profile.SpatialEnabled&&!art;
        BypassButton.Content=profile.SpatialEnabled||art?"Bypass tone":"Bypass";
        SpatialApplyButton.Content=art?"Apply 16ch profile":"Apply 7.1 rendering";
        ArtProfileText.Text=art?"Loaded: "+System.IO.Path.GetFileNameWithoutExtension(profile.Art16ProfilePath):"No official 16-channel preset loaded.";
    }
    void RefreshRoutes()
    {
        var inputId=(RelayInputBox.SelectedItem as RouteEndpoint)?.Id;var outputId=(RelayOutputBox.SelectedItem as RouteEndpoint)?.Id;var processingId=(SpatialPlaybackBox.SelectedItem as RouteEndpoint)?.Id;
        var playback=RelayService.List(false);var inputs=RelayService.List(true);var physical=playback.Where(p=>!p.Virtual&&p.Channels>=2).ToArray();var virtuals=playback.Where(p=>p.Virtual&&p.Channels>=2).ToArray();
        RelayInputBox.ItemsSource=inputs;RelayInputBox.SelectedItem=inputs.FirstOrDefault(d=>d.Id==inputId)??inputs.FirstOrDefault();
        RelayOutputBox.ItemsSource=physical;var defaultId=EndpointService.Enumerate().FirstOrDefault(d=>d.IsDefault)?.Id;
        RelayOutputBox.SelectedItem=physical.FirstOrDefault(d=>d.Id==outputId)??physical.FirstOrDefault(d=>d.Id==defaultId)??physical.FirstOrDefault();
        SpatialPlaybackBox.ItemsSource=virtuals;SpatialPlaybackBox.SelectedItem=virtuals.FirstOrDefault(d=>d.Id==processingId)??virtuals.FirstOrDefault(d=>d.Id==profile.DeviceId)??virtuals.FirstOrDefault();
        if(profile.SpatialEnabled)RelayModeBox.SelectedIndex=1;else if(!string.IsNullOrWhiteSpace(profile.Art16ProfilePath))RelayModeBox.SelectedIndex=2;
        SpatialSetupText.Text=virtuals.Length==0?"No virtual playback output is installed. Set up the virtual cable below, restart if requested, then Refresh devices.":"Virtual outputs found. For OpenCue 7.1, choose an output configured as eight channels / 48 kHz and attach Equalizer APO to it. The installation test still needs to pass.";
        RelayStatusText.Text=inputs.Count==0?"No supported virtual capture input was found. Normal stereo tuning still works through the Sound tab.":"Select matching virtual input and physical headphones. Route the game's output to the virtual playback device, then Start game audio.";
        RefreshRelayUi();UpdateSpatialInfo();
    }
    void RefreshRelayUi()
    {
        RelayStartButton.IsEnabled=!relay.Running&&RelayInputBox.SelectedItem is not null&&RelayOutputBox.SelectedItem is not null;
        RelayStopButton.IsEnabled=relay.Running;RelayInputBox.IsEnabled=!relay.Running;RelayOutputBox.IsEnabled=!relay.Running;RelayModeBox.IsEnabled=!relay.Running;RelayLatencySlider.IsEnabled=!relay.Running;
        if(!relay.Running)RelayMeter.Value=0;
    }
    void SelectProcessingOutput(string id)
    {
        RefreshDevices();var items=DeviceBox.ItemsSource as IEnumerable<RenderEndpoint>;var selected=items?.FirstOrDefault(d=>d.Id==id)??throw new InvalidOperationException("The chosen virtual playback output is disconnected. Refresh devices.");
        DeviceBox.SelectedItem=selected;profile.DeviceId=id;
    }
    void ApplySpatial()
    {
        var endpoint=SpatialPlaybackBox.SelectedItem as RouteEndpoint??throw new InvalidOperationException("Choose the virtual processing output first.");
        ReadControls();profile.AllDevices=false;profile.Enabled=true;
        if(!string.IsNullOrWhiteSpace(profile.Art16ProfilePath)){profile.SpatialEnabled=false;RelayModeBox.SelectedIndex=2;}
        else{profile.SpatialEnabled=true;RelayModeBox.SelectedIndex=1;}
        SelectProcessingOutput(endpoint.Id);LoadControls();ApplySound();if(!lastApplySucceeded)return;
        SpatialSetupText.Text="Spatial configuration saved. Confirm this virtual output passes Device Selector's installation test. Route the game to it, choose the matching capture input below, and Start game audio.";
    }
    void ReturnToStereo()
    {
        relay.Stop();RefreshRelayUi();ReadControls();profile.SpatialEnabled=false;profile.Art16ProfilePath=null;profile.AllDevices=false;profile.Enabled=true;
        var physical=RelayOutputBox.SelectedItem as RouteEndpoint??RelayService.List(false).FirstOrDefault(d=>!d.Virtual&&d.Channels>=2)??throw new InvalidOperationException("Connect your physical headphones, then Refresh devices.");
        SelectProcessingOutput(physical.Id);LoadControls();RelayModeBox.SelectedIndex=0;ApplySound();if(!lastApplySucceeded){RelayStatusText.Text="The previous audio controller was kept. Select your physical headphones in the game for normal playback.";return;}
        RelayStatusText.Text="Stereo tuning applied to your physical output. Choose that same output inside the game. The virtual audio relay is stopped.";
    }
    void LoadArtProfile()
    {
        var dialog=new OpenFileDialog{Title="Choose an installed official ArtTune 16ch profile",Filter="ArtTune 16ch profile (*.txt)|*.txt"};if(dialog.ShowDialog(this)!=true)return;
        var art=ArtProfileService.Validate(dialog.FileName);profile.Art16ProfilePath=art.Path;profile.SpatialEnabled=false;profile.AllDevices=false;profile.Enabled=true;LoadControls();RelayModeBox.SelectedIndex=2;
        var targets=(SpatialPlaybackBox.ItemsSource as IEnumerable<RouteEndpoint>)?.Where(d=>d.Channels==16).ToArray();if(targets?.Length==1)SpatialPlaybackBox.SelectedItem=targets[0];
        ArtProfileText.Text="Loaded: "+art.Name+". The profile and engine remain unmodified.";RelayStatusText.Text="Select its matching 16-channel virtual playback output above and Apply 16ch profile. Then select the matching virtual capture and your headphones below.";
    }
    void StartRelay()
    {
        var input=RelayInputBox.SelectedItem as RouteEndpoint??throw new InvalidOperationException("Choose the virtual cable capture input.");var physical=RelayOutputBox.SelectedItem as RouteEndpoint??throw new InvalidOperationException("Choose your physical headphones as the final output.");
        int mode=RelayModeBox.SelectedIndex;if(mode<0)throw new InvalidOperationException("Choose the audio route mode.");
        if(mode==1)
        {
            RequireEngine();var target=SpatialPlaybackBox.SelectedItem as RouteEndpoint??throw new InvalidOperationException("Choose and apply the 7.1 virtual processing output above.");RelayService.ValidateSpatialEndpoint(target.Id);
            var scope="Device: "+new RenderEndpoint(target.Id,target.Name).GuidSelector;
            if(!File.Exists(engine!.ActiveFile)||!File.ReadAllText(engine.ActiveFile).Contains("OpenCueSpatial-v1.dll",StringComparison.Ordinal)||!File.ReadAllLines(engine.ActiveFile).Any(line=>line.Trim().Equals(scope,StringComparison.OrdinalIgnoreCase))||!File.ReadAllText(engine.RootFile).Contains(EngineService.Begin,StringComparison.Ordinal))throw new InvalidOperationException("Apply 7.1 rendering to this selected virtual output before starting the relay. Confirm its installation test and choose that cable's matching capture input.");
        }
        if(mode==2)
        {
            RequireEngine();var target=SpatialPlaybackBox.SelectedItem as RouteEndpoint??throw new InvalidOperationException("Choose the official 16-channel virtual playback output above.");RelayService.ValidateArtEndpoint(target.Id);
            if(string.IsNullOrWhiteSpace(profile.Art16ProfilePath))throw new InvalidOperationException("Load the installed official 16ch preset and apply it before starting this route. The matching official engine is required.");
            var art=ArtProfileService.Validate(profile.Art16ProfilePath);
            var scope="Device: "+new RenderEndpoint(target.Id,target.Name).GuidSelector;
            if(!File.Exists(engine!.ActiveFile)||!File.ReadAllText(engine.ActiveFile).Contains("Include: "+art.Path,StringComparison.OrdinalIgnoreCase)||!File.ReadAllLines(engine.ActiveFile).Any(line=>line.Trim().Equals(scope,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Apply the selected official 16ch profile to this virtual output before starting the relay. Choose that cable's matching capture input.");
        }
        relay.Start(input,physical,mode==0?2:mode==1?8:16,(int)RelayLatencySlider.Value,RelayVolumeSlider.Value);routeLabel=input.Name+" → "+physical.Name;RefreshRelayUi();
    }
    async Task SetupCable()
    {
        CableButton.IsEnabled=false;
        try
        {
            var installer=await SetupService.DownloadCableInstaller(text=>SpatialSetupText.Text=text);
            SpatialSetupText.Text="The verified VB-CABLE installer is opening. Approve Windows and select Install Driver. Restart Windows when the driver asks, then reopen OpenCue and Refresh devices.";
            using var process=Process.Start(new ProcessStartInfo(installer){UseShellExecute=true,Verb="runas",WorkingDirectory=System.IO.Path.GetDirectoryName(installer)!});if(process is not null)await process.WaitForExitAsync();
            RefreshRoutes();
        }
        catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){SpatialSetupText.Text="Cable setup was cancelled. Normal stereo tuning remains available.";}
        catch(Exception ex){SpatialSetupText.Text=ex.Message;SetupService.OpenUrl("https://vb-audio.com/Cable/");}
        finally{CableButton.IsEnabled=true;}
    }
    void RefreshStatus()
    {
        engine=EngineService.Detect();
        if(engine is null)
        {
            EngineText.Text="Free engine setup needed"; SetupProgressText.Text="Step 1 of 3 · Connect your headphones. Start setup to get the free processor.";
            SelectorButton.IsEnabled=false; BypassButton.IsEnabled=false; RestoreButton.IsEnabled=false; OpenConfigButton.IsEnabled=false;
        }
        else
        {
            bool integrated=File.Exists(engine.ActiveFile) && File.ReadAllText(engine.RootFile).Contains(EngineService.Begin);
            EngineText.Text=integrated ? "Configuration saved · test attachment" : "Installed · check output attachment";
            SetupProgressText.Text="Step 2 of 3 · The processor is installed. In Device Selector, select your playback output and run its installation test. Then choose Sound → Apply sound.";
            SelectorButton.IsEnabled=engine.Selector is not null; BypassButton.IsEnabled=integrated; RestoreButton.IsEnabled=integrated; OpenConfigButton.IsEnabled=true;
        }
    }
    void ApplySound()
    {
        lastApplySucceeded=false;
        ReadControls(); profile.Validate(); RequireEngine();
        if(!profile.Enabled&&(profile.SpatialEnabled||profile.Art16ProfilePath is not null))throw new InvalidOperationException("Keep the spatial renderer running for a multichannel route. Use Bypass tone to compare EQ/dynamics, or Return to stereo to leave spatial mode.");
        if(!profile.AllDevices && profile.Enabled && !EndpointService.Enumerate().Any(d=>d.Id==profile.DeviceId)) throw new InvalidOperationException("The selected playback output is no longer connected. Connect it, then select Refresh before applying.");
        if(profile.Enabled)foreach(var target in profile.AllDevices?EndpointService.Enumerate():new[]{DeviceBox.SelectedItem as RenderEndpoint??throw new InvalidOperationException("Choose an active playback output.")})RelayService.ValidateToneEndpoint(target.Id,profile.Correction?.MaximumFrequency??0);
        if(profile.Enabled && profile.SafePreamp < -24) throw new InvalidOperationException("These combined boosts need more than 24 dB of headroom. Reduce the positive EQ gains, then apply again.");
        if(profile.Enabled && profile.PreampDb>profile.SafePreamp) {profile.PreampDb=profile.SafePreamp;LoadControls();}
        try{StatusText.Text=EngineService.Apply(engine!,profile,DeviceBox.SelectedItem as RenderEndpoint);}
        catch(IOException)when(NeedsControlledSetup())
        {
            var answer=MessageBox.Show(this,"Your existing Equalizer APO configuration uses a routing or text format that OpenCue cannot safely extend.\n\nUse OpenCue as the audio controller? This replaces the main configuration for all devices and keeps exact recovery backups. Existing APO effects stop until you restore the previous setup.\n\nSelected output: "+(DeviceBox.SelectedItem?.ToString()??"All playback outputs")+"\n\nRemove tuning can restore that backup while no newer external edits have been made.","Switch audio controller",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No);
            if(answer!=MessageBoxResult.Yes){StatusText.Text="The previous audio controller was kept. No configuration was replaced.";return;}
            StatusText.Text=EngineTakeoverService.Apply(engine!,profile,DeviceBox.SelectedItem as RenderEndpoint);
        }
        SaveState();RefreshStatus();lastApplySucceeded=true;
    }
    bool NeedsControlledSetup()
    {
        if(engine is null)return false;
        var original=EngineService.Decode(File.ReadAllBytes(engine.RootFile));
        return original.Encoding is UnicodeEncoding||original.Text.Contains('\0')||original.Text.Split('\n').Any(line=>line.TrimStart().StartsWith("Stage:",StringComparison.OrdinalIgnoreCase)||line.TrimStart().StartsWith("If:",StringComparison.OrdinalIgnoreCase)||line.TrimStart().StartsWith("Else:",StringComparison.OrdinalIgnoreCase));
    }
    void ImportCorrection()
    {
        var dialog=new OpenFileDialog{Title="Import headphone EQ",Filter="Equalizer APO headphone correction (*.txt)|*.txt"};if(dialog.ShowDialog(this)!=true)return;
        if(new FileInfo(dialog.FileName).Length>100000)throw new InvalidDataException("Choose a correction text file smaller than 100 KB.");
        var correction=CorrectionService.Parse(File.ReadAllText(dialog.FileName));profile.HeadphoneCorrection=correction.NormalizedText;profile.CorrectionName=System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);LoadControls();Changed();
    }
    void SaveState()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stateFile)!);
        EngineService.Atomic(stateFile,Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile,AudioProfile.JsonOptions)));
    }
    void Export()
    {
        ReadControls();profile.Validate();
        var dialog=new SaveFileDialog{Title="Save your OpenCue preset",Filter="OpenCue preset (*.json)|*.json",FileName="OpenCue-preset.json"};
        if(dialog.ShowDialog(this)==true){File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(profile,AudioProfile.JsonOptions));StatusText.Text="Preset exported. It contains settings only; it does not include any paid software.";}
    }
    void Import()
    {
        var dialog=new OpenFileDialog{Title="Open an OpenCue preset",Filter="OpenCue preset (*.json)|*.json"};
        if(dialog.ShowDialog(this)!=true)return;
        if(new FileInfo(dialog.FileName).Length>1048576)throw new InvalidDataException("This preset is too large.");
        var imported=JsonSerializer.Deserialize<AudioProfile>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("The preset is empty.");imported.Validate();
        imported.DeviceId=profile.DeviceId;imported.AllDevices=false;imported.SpatialEnabled=profile.SpatialEnabled;imported.Art16ProfilePath=profile.Art16ProfilePath;profile=imported;LoadControls();StatusText.Text="Preset imported. Check your output and select Apply sound.";
    }
    async Task StartSetup()
    {
        GetEngineButton.IsEnabled=false;MainTabs.SelectedIndex=1;
        try
        {
            engine=EngineService.Detect();
            if(engine is not null){OpenSelector();return;}
            var installer=await SetupService.DownloadInstaller(text=>SetupProgressText.Text=text);
            SetupProgressText.Text="Step 1 of 3 · The verified installer is opening. Approve Windows if asked, keep the default folder, and choose your headphones in the playback list. Restart only if requested.";
            using var process=Process.Start(new ProcessStartInfo(installer){UseShellExecute=true});
            if(process is not null)await process.WaitForExitAsync();
            RefreshStatus();
            if(engine is null)SetupProgressText.Text="Setup has closed. If you cancelled it, select Start setup again. If installation completed, restart if requested, reopen OpenCue and select Refresh.";
        }
        catch(Exception ex){SetupProgressText.Text=ex.Message;StatusText.Text=ex.Message;SetupService.OpenUrl(SetupService.DownloadPage);}
        finally{GetEngineButton.IsEnabled=true;}
    }
    async Task OpenLeq()
    {
        if(leqPath is null){LeqStatusText.Text="Choose the supplied tools/LEQControlPanel.exe first. This is an optional control panel.";return;}
        LeqButton.IsEnabled=false;LocateLeqButton.IsEnabled=false;
        try{LeqStatusText.Text="Checking the official file, then opening its control panel…";await LeqService.ValidateAndOpen(leqPath);LeqStatusText.Text="LEQ Control Panel opened. Select your output there. OpenCue has not toggled Windows LEQ.";}
        catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){LeqStatusText.Text="Administrator approval was cancelled. No LEQ setting was changed by OpenCue.";}
        catch(Exception ex){LeqStatusText.Text=ex.Message;}
        finally{LeqButton.IsEnabled=true;LocateLeqButton.IsEnabled=true;}
    }
    void OpenSelector()
    {
        RequireEngine();if(engine!.Selector is null)throw new FileNotFoundException("The free processor's Device Selector was not found. Repair its installation from the official project page.");
        SetupProgressText.Text="Step 2 of 3 · Select the same playback output shown in Sound. Leave microphones alone. Run Device Selector's installation test, follow any restart prompt, then return to Sound and Apply sound.";
        Process.Start(new ProcessStartInfo(engine.Selector){UseShellExecute=true});
    }
    async Task PlayChannel(bool right)
    {
        TestLeftButton.IsEnabled=false;TestRightButton.IsEnabled=false;
        try
        {
            StatusText.Text="Playing a short, low-level "+(right?"right":"left")+" sample through the Windows default output. This does not verify the audio engine.";
            SetupProgressText.Text=StatusText.Text;
            await Task.Run(()=>{using var wave=new MemoryStream(CreateTone(right));using var player=new SoundPlayer(wave);player.PlaySync();});
        }
        catch(Exception ex){StatusText.Text="Sample playback failed: "+ex.Message;SetupProgressText.Text=StatusText.Text;}
        finally{TestLeftButton.IsEnabled=true;TestRightButton.IsEnabled=true;}
    }
    static byte[] CreateTone(bool right)
    {
        const int rate=48000,count=16800;using var memory=new MemoryStream();using var writer=new BinaryWriter(memory);
        writer.Write("RIFF"u8.ToArray());writer.Write(36+count*4);writer.Write("WAVEfmt "u8.ToArray());writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(rate);writer.Write(rate*4);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8.ToArray());writer.Write(count*4);
        for(int i=0;i<count;i++){double fade=Math.Min(1,Math.Min(i/960.0,(count-1-i)/960.0));short sample=(short)(Math.Sin(2*Math.PI*480*i/rate)*fade*1300);writer.Write(right?(short)0:sample);writer.Write(right?sample:(short)0);}return memory.ToArray();
    }
    void DrawCurve()
    {
        if(CurveCanvas is null || bands[0] is null || CurveCanvas.ActualWidth<=0)return;
        CurveCanvas.Children.Clear();var line=new Polyline{Stroke=(Brush)FindResource("AccentBrush"),StrokeThickness=2.5};
        double width=CurveCanvas.ActualWidth,height=CurveCanvas.ActualHeight;
        for(int i=0;i<320;i++)
        {
            double f=20*Math.Pow(1000,i/319.0),db=0;
            for(int b=0;b<10;b++)
            {
                double a=Math.Pow(10,bands[b].Value/40),w0=2*Math.PI*AudioProfile.Frequencies[b]/48000,alpha=Math.Sin(w0)/(2*1.4),cw=Math.Cos(w0);
                Complex z=Complex.FromPolarCoordinates(1,-2*Math.PI*f/48000),z2=z*z;
                var numerator=(1+alpha*a)+(-2*cw)*z+(1-alpha*a)*z2;
                var denominator=(1+alpha/a)+(-2*cw)*z+(1-alpha/a)*z2;
                db+=20*Math.Log10((numerator/denominator).Magnitude);
            }
            line.Points.Add(new Point(width*i/319, height*(0.5-Math.Clamp(db,-12,12)/24)));
        }
        CurveCanvas.Children.Add(line);
    }
}
