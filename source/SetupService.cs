using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;

namespace OpenCue;
public static class SetupService
{
    public const string DownloadPage = "https://sourceforge.net/projects/equalizerapo/files/1.4.2/EqualizerAPO-x64-1.4.2.exe/download";
    // Pinned to the original official SourceForge download verified 16 October 2026.
    public const string InstallerHash = "7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA";
    public const string CableDownload="https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    public const string CableHash="B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(120) };
    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public static async Task<string> DownloadInstaller(Action<string> progress)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenCue", "downloads");
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "EqualizerAPO-x64-1.4.2.exe");
        if (File.Exists(path) && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) == InstallerHash) return path;
        progress("Step 1 of 3 · Downloading the free audio processor from its official project…");
        byte[] bytes = await Fetch(DownloadPage);
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z')
        {
            string html = Encoding.UTF8.GetString(bytes);
            var link = Regex.Match(html, @"https://downloads\.sourceforge\.net/project/equalizerapo/1\.4\.2/EqualizerAPO-x64-1\.4\.2\.exe\?[^""<>\s]+");
            if (!link.Success) throw new IOException("The download site did not provide an installer. The official page will open so you can download it there.");
            bytes = await Fetch(WebUtility.HtmlDecode(link.Value));
        }
        if (bytes.Length < 1000000 || bytes[0] != 'M' || bytes[1] != 'Z' || Convert.ToHexString(SHA256.HashData(bytes)) != InstallerHash) throw new IOException("The installer did not match the verified release. It was not run. Use the official project page instead.");
        EngineService.Atomic(path, bytes); return path;
    }
    static async Task<byte[]> Fetch(string url)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new IOException("The download was redirected to an insecure connection. Open the official page instead.");
        if (response.Content.Headers.ContentLength > 30000000) throw new IOException("The download is larger than the expected release.");
        using var stream = await response.Content.ReadAsStreamAsync(); using var result = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(buffer)) > 0) { if (result.Length + count > 30000000) throw new IOException("The download exceeded the expected size."); result.Write(buffer, 0, count); }
        return result.ToArray();
    }
    public static async Task<string> DownloadCableInstaller(Action<string> progress)
    {
        progress("Downloading the optional virtual cable from VB-Audio's official site…");
        byte[] bytes=await Fetch(CableDownload);
        if(Convert.ToHexString(SHA256.HashData(bytes))!=CableHash)throw new IOException("The virtual-cable package did not match the verified release. It was not run; obtain it directly from VB-Audio.");
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenCue","downloads","cable-pack45");Directory.CreateDirectory(folder);
        using var memory=new MemoryStream(bytes);using var archive=new ZipArchive(memory,ZipArchiveMode.Read);archive.ExtractToDirectory(folder,true);
        var installer=Path.Combine(folder,"VBCABLE_Setup_x64.exe");if(!File.Exists(installer))throw new IOException("The verified cable package did not contain its x64 installer.");return installer;
    }
}
