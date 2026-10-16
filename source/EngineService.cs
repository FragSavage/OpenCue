using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenCue;

public sealed record EngineLocation(string InstallPath, string ConfigPath)
{
    public string RootFile => Path.Combine(ConfigPath, "config.txt");
    public string OwnDirectory => Path.Combine(ConfigPath, "OpenCue");
    public string ActiveFile => Path.Combine(OwnDirectory, "active.txt");
    public string? Selector => new[] { "DeviceSelector.exe", "Configurator.exe" }.Select(n => Path.Combine(InstallPath, n)).FirstOrDefault(File.Exists);
}

public sealed class EngineService
{
    internal const string Begin = "# >>> OpenCue managed block >>>";
    internal const string End = "# <<< OpenCue managed block <<<";
    const string Header = "# OpenCue-owned configuration v1";
    public static string PluginSource => Path.Combine(AppContext.BaseDirectory, "engine", "OpenCueDynamics.dll");
    static string EnsurePlugin()=>ExtractNative("OpenCue.Dynamics.dll","OpenCueDynamics-v2.dll","OpenCueDynamics.dll");
    static string EnsureSpatial()=>ExtractNative("OpenCue.Spatial.dll","OpenCueSpatial-v1.dll","OpenCueSpatial.dll");
    static string ExtractNative(string resourceName,string cacheName,string sideBySide)
    {
        var bundled=Path.Combine(AppContext.BaseDirectory,"engine",sideBySide);if(File.Exists(bundled))return bundled;
        using var resource = typeof(EngineService).Assembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException("An original audio-processing resource is missing from this build.");
        using var data = new MemoryStream(); resource.CopyTo(data); var bytes = data.ToArray();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenCue", "engine");
        Directory.CreateDirectory(folder); var path = Path.Combine(folder,cacheName);
        if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(bytes))) Atomic(path, bytes);
        return path;
    }
    public static EngineLocation? Detect()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            if (key?.GetValue("InstallPath") is string install)
            {
                var config = key.GetValue("ConfigPath") as string ?? Path.Combine(install, "config");
                if (ValidEngine(install, config)) return new(install, config);
            }
        }
        foreach (var parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var install = Path.Combine(parent, "EqualizerAPO"); var config = Path.Combine(install, "config");
            if (ValidEngine(install, config)) return new(install, config);
        }
        return null;
    }
    static bool ValidEngine(string install, string config) => Path.IsPathFullyQualified(install) && Path.IsPathFullyQualified(config) && File.Exists(Path.Combine(install, "EqualizerAPO.dll")) && File.Exists(Path.Combine(config, "config.txt"));
    static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    public static string Compile(AudioProfile profile, RenderEndpoint? endpoint, string pluginPath, IReadOnlyList<RenderEndpoint>? playbackEndpoints = null,string? spatialPluginPath=null)
    {
        profile.Validate();
        if (!profile.Enabled) return Header + "\r\n# Bypassed. Other audio configuration is unchanged.\r\n";
        if (!profile.AllDevices && endpoint is null) throw new InvalidOperationException("Choose an active stereo headphone output first.");
        if (profile.AllDevices && (playbackEndpoints is null || playbackEndpoints.Count == 0)) throw new InvalidOperationException("No active playback outputs were supplied for all-output mode.");
        if (profile.PreampDb > profile.SafePreamp) throw new InvalidOperationException($"Use at least {Number(-profile.SafePreamp)} dB of headroom for these boosts. Reduce the boosts if more than 24 dB is needed.");
        if (pluginPath.IndexOfAny(['\r','\n','"']) >= 0 || !Path.IsPathFullyQualified(pluginPath)) throw new InvalidDataException("Invalid dynamics library path.");
        var sb = new StringBuilder(Header + "\r\n# Original stereo EQ and dynamics; no source separation or virtual surround.\r\n");
        sb.AppendLine("Device: " + (profile.AllDevices ? string.Join("; ", playbackEndpoints!.Select(d=>d.GuidSelector).Distinct(StringComparer.OrdinalIgnoreCase)) : endpoint!.GuidSelector));
        if(!string.IsNullOrWhiteSpace(profile.Art16ProfilePath))
        {
            var art=ArtProfileService.Validate(profile.Art16ProfilePath);sb.AppendLine("# Official ArtTune profile supplied by this device's user; retained unmodified.");sb.AppendLine("Channel: all");sb.AppendLine("Include: "+art.Path);sb.AppendLine("Device: "+endpoint!.GuidSelector);
        }
        if(profile.SpatialEnabled)
        {
            if(spatialPluginPath is null || !Path.IsPathFullyQualified(spatialPluginPath) || spatialPluginPath.IndexOfAny(['\r','\n','"'])>=0)throw new InvalidDataException("A valid spatial processor path is required.");
            if(profile.TransientAmount>0){sb.AppendLine("# Reduce front-channel transients before binaural rendering.");sb.AppendLine("Channel: L R");sb.AppendLine(NativeCommand(new AudioProfile{LimiterEnabled=false,TransientAmount=profile.TransientAmount},pluginPath));}
            sb.AppendLine("Channel: L R C LFE RL RR SL SR");sb.AppendLine($"VSTPlugin: Library \"{spatialPluginPath}\"");
        }
        sb.AppendLine("Channel: L R");
        if(profile.Correction is CorrectionData correction)sb.AppendLine(correction.NormalizedText);
        sb.AppendLine("Preamp: " + Number(profile.PreampDb) + " dB");
        for (int i = 0; i < 10; i++) sb.AppendLine($"Filter {i+1}: ON PK Fc {Number(AudioProfile.Frequencies[i])} Hz Gain {Number(profile.Gains[i])} dB Q 1.4");
        if (profile.NeedsNative) sb.AppendLine(NativeCommand(profile,pluginPath,profile.SpatialEnabled?0:null));
        sb.AppendLine("Device: all"); sb.AppendLine("Channel: all");
        return sb.ToString();
    }
    static string NativeCommand(AudioProfile profile,string pluginPath,double? transient=null)=>$"VSTPlugin: Library \"{pluginPath}\" Threshold {Number((profile.ThresholdDb+30)/30)} Ratio {Number((profile.Ratio-1)/7)} Ceiling {Number((profile.CeilingDb+12)/11.5)} CompOn {(profile.DynamicsEnabled?1:0)} Attack {Number((profile.AttackMs-1)/49)} Release {Number((profile.ReleaseMs-20)/480)} Makeup {Number(profile.MakeupDb/12)} Transient {Number(transient??profile.TransientAmount)} Ambience {Number(profile.AmbienceAmount)} Floor {Number((profile.FloorDb+80)/60)} Leveling {Number(profile.LevelingAmount)} Width {Number(profile.Width/2)} Balance {Number((profile.Balance+1)/2)} LimitOn {(profile.LimiterEnabled?1:0)}";
    public static string Apply(EngineLocation engine, AudioProfile profile, RenderEndpoint? endpoint)
    {
        var plugin = Path.Combine(engine.OwnDirectory, "OpenCueDynamics-v2.dll");
        var spatialPlugin=Path.Combine(engine.OwnDirectory,"OpenCueSpatial-v1.dll");
        var config = Compile(profile, endpoint, plugin, profile.AllDevices ? EndpointService.Enumerate() : null,spatialPlugin);
        if(profile.Enabled&&profile.SpatialEnabled)RelayService.ValidateSpatialEndpoint(endpoint!.Id);
        if(profile.Enabled&&!string.IsNullOrWhiteSpace(profile.Art16ProfilePath))RelayService.ValidateArtEndpoint(endpoint!.Id);
        var original = File.ReadAllBytes(engine.RootFile);
        var decoded = Decode(original);
        if (decoded.Encoding is UnicodeEncoding || decoded.Text.Contains('\0')) throw new IOException("Equalizer APO requires a UTF-8 or Windows text configuration. This file is UTF-16 or contains NUL bytes; convert it in Configuration Editor before applying. The file was left unchanged.");
        CheckMarkers(decoded.Text);
        EnsureNoDuplicateInclude(decoded.Text, engine);
        Directory.CreateDirectory(engine.OwnDirectory);
        if (File.Exists(engine.ActiveFile) && !File.ReadAllText(engine.ActiveFile).StartsWith(Header)) throw new IOException("OpenCue's target config filename is used by another file. No audio settings were changed.");
        if (profile.Enabled && profile.NeedsNative)
        {
            if (!IsX64Library(Path.Combine(engine.InstallPath, "EqualizerAPO.dll"))) throw new IOException("This dynamics processor needs the x64 edition of Equalizer APO. EQ alone can be used with other editions; switch dynamics off or install x64 on a compatible PC.");
            var pluginSource = EnsurePlugin();
            if (!File.Exists(plugin)) File.Copy(pluginSource, plugin);
            else if (!SHA256.HashData(File.ReadAllBytes(plugin)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(pluginSource)))) throw new IOException("A different dynamics DLL already occupies the versioned path. Restore this app's integration before updating.");
        }
        if(profile.Enabled&&profile.SpatialEnabled)
        {
            if(!IsX64Library(Path.Combine(engine.InstallPath,"EqualizerAPO.dll")))throw new IOException("7.1 rendering requires the x64 edition of Equalizer APO.");
            var spatialSource=EnsureSpatial();
            if(!File.Exists(spatialPlugin))File.Copy(spatialSource,spatialPlugin);
            else if(!SHA256.HashData(File.ReadAllBytes(spatialPlugin)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(spatialSource))))throw new IOException("A different spatial DLL already occupies this versioned path. Review it before updating.");
        }
        var backupPath = Path.Combine(engine.OwnDirectory, "config.before-OpenCue.bin");
        if (!File.Exists(backupPath)) File.WriteAllBytes(backupPath, original);
        // The owned file is initially bypassed before adding its root include.
        // It can only be enabled after the root write completes successfully.
        if (!File.Exists(engine.ActiveFile)) Atomic(engine.ActiveFile, Encoding.UTF8.GetBytes(Header + "\r\n# Not applied yet.\r\n"));
        if (!decoded.Text.Contains(Begin))
        {
            var nl = decoded.Text.Contains("\r\n") ? "\r\n" : "\n";
            string separator = decoded.Text.EndsWith("\n") || decoded.Text.Length == 0 ? "" : nl;
            var block = separator + ManagedBlock(nl) + nl;
            var updated = Encode(decoded, decoded.Text + block);
            if (!original.SequenceEqual(File.ReadAllBytes(engine.RootFile))) throw new IOException("config.txt changed while applying. Try again after the other editor finishes.");
            Atomic(engine.RootFile, updated);
            File.WriteAllText(Path.Combine(engine.OwnDirectory, "integration.json"), JsonSerializer.Serialize(new { SeparatorAdded = separator.Length > 0 }));
        }
        Atomic(engine.ActiveFile, new UTF8Encoding(false).GetBytes(config));
        EngineTakeoverService.RecordManagedUpdate(engine);
        return "Configuration applied. Confirm Equalizer APO is attached to this output; an audible hardware check is still needed.";
    }
    public static void Bypass(EngineLocation engine)
    {
        if (!File.Exists(engine.ActiveFile)) throw new InvalidOperationException("No OpenCue configuration has been applied to this engine.");
        if (!File.ReadAllText(engine.ActiveFile).StartsWith(Header)) throw new IOException("The active file is not owned by OpenCue; it was left untouched.");
        Atomic(engine.ActiveFile, Encoding.UTF8.GetBytes(Header + "\r\n# Bypassed. Other effects are unchanged.\r\n"));
        EngineTakeoverService.RecordManagedUpdate(engine);
    }
    public static void Restore(EngineLocation engine)
    {
        if(EngineTakeoverService.TryRestore(engine))return;
        var original = File.ReadAllBytes(engine.RootFile); var decoded = Decode(original);
        CheckMarkers(decoded.Text, false);
        var start = decoded.Text.IndexOf(Begin, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("There is no OpenCue integration to remove.");
        var end = decoded.Text.IndexOf(End, start, StringComparison.Ordinal) + End.Length;
        if (decoded.Text.AsSpan(end).StartsWith("\r\n")) end += 2; else if (decoded.Text.AsSpan(end).StartsWith("\n")) end++;
        if (File.Exists(engine.ActiveFile)) Bypass(engine);
        var prefix = decoded.Text[..start]; var suffix = decoded.Text[end..];
        var metadataPath = Path.Combine(engine.OwnDirectory, "integration.json");
        if (suffix.Length == 0 && File.Exists(metadataPath))
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (metadata.RootElement.GetProperty("SeparatorAdded").GetBoolean())
            {
                if (prefix.EndsWith("\r\n")) prefix = prefix[..^2]; else if (prefix.EndsWith("\n")) prefix = prefix[..^1];
            }
        }
        var restored = prefix + suffix;
        if (!original.SequenceEqual(File.ReadAllBytes(engine.RootFile))) throw new IOException("config.txt changed during restore. Try again.");
        Atomic(engine.RootFile, Encode(decoded, restored));
        // Keep the original backup and the now-bypassed owned file for recovery.
    }
    static string ManagedBlock(string nl) => string.Join(nl, Begin, "Device: all", "Channel: all", "Include: OpenCue\\active.txt", "Device: all", "Channel: all", End);
    static void CheckMarkers(string text, bool requireFinal = true)
    {
        int countStart = text.Split(Begin).Length-1, countEnd = text.Split(End).Length-1;
        if (countStart != countEnd || countStart > 1 || countStart == 1 && text.IndexOf(Begin, StringComparison.Ordinal) > text.IndexOf(End, StringComparison.Ordinal)) throw new IOException("The OpenCue marker block is damaged or duplicated. Review config.txt; no root changes were made.");
        if (countStart == 1)
        {
            int start=text.IndexOf(Begin,StringComparison.Ordinal), end=text.IndexOf(End,StringComparison.Ordinal)+End.Length;
            if (text[start..end].Replace("\r\n","\n") != ManagedBlock("\n")) throw new IOException("The owned OpenCue block was edited. Review that block in config.txt before applying or restoring; it was left unchanged.");
            if (requireFinal && text[end..].Split('\n').Any(line=>!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))) throw new IOException("Another effect follows OpenCue in config.txt. Move the OpenCue block to the end, or remove its integration before applying.");
        }
        // An Include appended inside a conditional would not be a dependable root chain.
        if (requireFinal && text.Split('\n').Any(line => line.TrimStart().StartsWith("If:", StringComparison.OrdinalIgnoreCase) || line.TrimStart().StartsWith("Else:", StringComparison.OrdinalIgnoreCase) || line.TrimStart().StartsWith("Stage:", StringComparison.OrdinalIgnoreCase))) throw new IOException("This config uses conditional or stage routing. Review and integrate it manually in Configuration Editor.");
    }
    static void EnsureNoDuplicateInclude(string root, EngineLocation engine)
    {
        if(root.Contains(Begin)) {int start=root.IndexOf(Begin,StringComparison.Ordinal),end=root.IndexOf(End,StringComparison.Ordinal)+End.Length;root=root[..start]+root[end..];}
        var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int files=0;
        var prefix=Path.GetFullPath(engine.ConfigPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        void Visit(string text,string directory)
        {
            if(++files>128)throw new IOException("This configuration has too many nested includes for automatic integration. Review it in Configuration Editor.");
            foreach(var line in text.Split('\n'))
            {
                var match=System.Text.RegularExpressions.Regex.Match(line,@"^\s*Include:\s*(.+?)\s*$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if(!match.Success)continue;
                string target=match.Groups[1].Value.Trim().Trim('"').Replace('/',Path.DirectorySeparatorChar);
                string path=Path.GetFullPath(target,directory);
                if(path.Equals(Path.GetFullPath(engine.ActiveFile),StringComparison.OrdinalIgnoreCase))throw new IOException("Another Include already loads OpenCue's active file. Remove the duplicate reference before applying.");
                if(path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) && visited.Add(path) && File.Exists(path))
                {
                    if(new FileInfo(path).Length>1048576)throw new IOException("A nested configuration is too large for automatic integration. Review it in Configuration Editor.");
                    Visit(Decode(File.ReadAllBytes(path)).Text,Path.GetDirectoryName(path)!);
                }
            }
        }
        Visit(root,engine.ConfigPath);
    }
    internal static bool IsX64Library(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream=File.OpenRead(path);using var reader=new BinaryReader(stream);
        if(stream.Length<64 || reader.ReadUInt16()!=0x5a4d)return false;
        stream.Position=0x3c;int offset=reader.ReadInt32();if(offset<64 || offset>stream.Length-6)return false;
        stream.Position=offset;return reader.ReadUInt32()==0x00004550 && reader.ReadUInt16()==0x8664;
    }
    internal static void Atomic(string path, byte[] bytes)
    {
        var temp = path + ".opencue-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal sealed record TextFile(string Text, Encoding Encoding, bool Bom);
    internal static TextFile Decode(byte[] bytes)
    {
        Encoding encoding = new UTF8Encoding(false, true); int skip = 0; bool bom = false;
        if (bytes.AsSpan().StartsWith(new byte[] {0xef,0xbb,0xbf})) { encoding=new UTF8Encoding(true,true); skip=3; bom=true; }
        else if (bytes.AsSpan().StartsWith(new byte[] {0xff,0xfe})) { encoding=new UnicodeEncoding(false,true,true); skip=2; bom=true; }
        else if (bytes.AsSpan().StartsWith(new byte[] {0xfe,0xff})) { encoding=new UnicodeEncoding(true,true,true); skip=2; bom=true; }
        return new(encoding.GetString(bytes, skip, bytes.Length-skip), encoding, bom);
    }
    internal static byte[] Encode(TextFile original, string text)
    {
        var content = original.Encoding.GetBytes(text);
        return original.Bom ? original.Encoding.GetPreamble().Concat(content).ToArray() : content;
    }
}
