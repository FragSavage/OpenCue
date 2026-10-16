using System.Text.RegularExpressions;

namespace OpenCue;
public sealed record ArtProfile(string Path,string LibraryPath,string Name);
public static class ArtProfileService
{
    public static ArtProfile Validate(string path)
    {
        if(!System.IO.Path.IsPathFullyQualified(path)||path.IndexOfAny(['\r','\n','"'])>=0||!File.Exists(path))throw new InvalidDataException("Choose an installed official ArtTune 16-channel .txt profile.");
        if(new FileInfo(path).Length>100000)throw new InvalidDataException("This is larger than a supported official 16-channel profile.");
        var commands=File.ReadAllLines(path).Select(line=>line.Trim()).Where(line=>line.Length>0&&!line.StartsWith('#')).ToArray();
        if(commands.Length!=1)throw new InvalidDataException("Use an unmodified BO7 V5 16-channel profile containing its single spatial-engine command. Other APO configuration files are not imported here.");
        var match=Regex.Match(commands[0],"^VSTPlugin:\\s+Library\\s+\"([^\"]+)\"\\s+ChunkData\\s+\"([A-Za-z0-9+/=]+)\"$",RegexOptions.IgnoreCase);
        if(!match.Success)throw new InvalidDataException("This profile is not in the supported official ArtTune 16-channel format.");
        string library=match.Groups[1].Value;
        if(!Regex.IsMatch(System.IO.Path.GetFileName(library),@"^atk_spatial_engine_bravo_v[0-9_]+\.dll$",RegexOptions.IgnoreCase)||!System.IO.Path.IsPathFullyQualified(library)||!File.Exists(library))throw new InvalidDataException("The matching official ArtTune Bravo engine is missing. Install the free official ArtTune components before loading this profile.");
        if(!EngineService.IsX64Library(library))throw new InvalidDataException("The selected ArtTune engine is not an x64 Windows audio library.");
        return new(path,library,System.IO.Path.GetFileNameWithoutExtension(path));
    }
}
