using System.Diagnostics;
using System.Security.Cryptography;

namespace OpenCue;
public static class LeqService
{
    public const string OfficialHash="4F70FD62283BA1451E9C609835F36BF9ED80F8AAC9D6A082A59BD757E642A9A9";
    public static string? Find()
    {
        string[] candidates=[Path.Combine(AppContext.BaseDirectory,"tools","LEQControlPanel.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","LEQControlPanel.exe")];
        return candidates.FirstOrDefault(File.Exists);
    }
    public static async Task ValidateAndOpen(string path)
    {
        await using var file=File.OpenRead(path);
        var hash=Convert.ToHexString(await SHA256.HashDataAsync(file));
        if(hash!=OfficialHash)throw new InvalidDataException("This file is not the verified LEQ Control Panel v1.0.0 supplied with OpenCue. Choose the supplied tools/LEQControlPanel.exe, or get updates directly from ArtIsWar's official repository.");
        file.Close();
        // No toggle/silent arguments: simply open the official GUI and leave choices to its user.
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
}
