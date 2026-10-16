using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenCue;

/// <summary>
/// Explicit whole-root takeover. Call Apply only after the user approves replacing
/// the existing effects chain; ordinary integration must use EngineService.Apply.
/// Recovery never overwrites a root or owned active file with an unknown hash.
/// </summary>
public static class EngineTakeoverService
{
    private const string OwnedHeader = "# OpenCue-owned configuration v1";
    private const string MinimalRoot = "# OpenCue controlled engine\r\n";
    private static readonly byte[] BypassedActive = Encoding.UTF8.GetBytes(
        OwnedHeader + "\r\n# Bypassed after restoring the previous audio chain.\r\n");
    private static readonly byte[] PlaceholderActive = Encoding.UTF8.GetBytes(
        OwnedHeader + "\r\n# Not applied yet.\r\n");
    private static readonly byte[] StandardBypass = Encoding.UTF8.GetBytes(
        OwnedHeader + "\r\n# Bypassed. Other effects are unchanged.\r\n");

    private sealed class Recovery
    {
        public Recovery() { }
        public int Version { get; set; } = 1;
        public string RootPath { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public string RootBeforeHash { get; set; } = "";
        public string RootLastHash { get; set; } = "";
        public string IntermediateHash { get; set; } = "";
        public string ExpectedRootHash { get; set; } = "";
        public bool ActiveExisted { get; set; }
        public string? ActiveBeforeHash { get; set; }
        public string? ActiveLastHash { get; set; }
        public string CompiledActiveHash { get; set; } = "";
        public bool IntegrationExisted { get; set; }
        public string? IntegrationBeforeHash { get; set; }
        public string? IntegrationLastHash { get; set; }
        public string Status { get; set; } = "Preparing";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed record Prepared(string Config, byte[] Root, byte[]? Active, byte[]? Integration);

    private static string PointerPath(EngineLocation engine) => Path.Combine(engine.OwnDirectory, "takeover.json");
    private static string IntegrationPath(EngineLocation engine) => Path.Combine(engine.OwnDirectory, "integration.json");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string? FileHash(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static byte[] ManagedRoot() => Encoding.UTF8.GetBytes(MinimalRoot + string.Join("\r\n",
        EngineService.Begin, "Device: all", "Channel: all", "Include: OpenCue\\active.txt",
        "Device: all", "Channel: all", EngineService.End) + "\r\n");

    public static string Apply(EngineLocation engine, AudioProfile profile, RenderEndpoint? endpoint) =>
        ApplyCore(engine, profile, endpoint, () => EngineService.Apply(engine, profile, endpoint));

    private static Prepared Prepare(EngineLocation engine, AudioProfile profile, RenderEndpoint? endpoint)
    {
        profile.Validate();
        if (!Path.IsPathFullyQualified(engine.ConfigPath) || !Path.IsPathFullyQualified(engine.InstallPath))
            throw new InvalidDataException("The audio engine has no valid absolute installation/configuration path.");

        IReadOnlyList<RenderEndpoint>? endpoints = null;
        if (profile.Enabled)
        {
            endpoints = EndpointService.Enumerate();
            if (profile.AllDevices && endpoints.Count == 0)
                throw new InvalidOperationException("No active playback outputs are available for takeover.");
            if (!profile.AllDevices && (endpoint is null || !endpoints.Any(e => Same(e.Id, endpoint.Id))))
                throw new InvalidOperationException("The chosen playback output is no longer active. Refresh outputs before replacing the existing chain.");
        }

        var dynamics = Path.Combine(engine.OwnDirectory, "OpenCueDynamics-v2.dll");
        var spatial = Path.Combine(engine.OwnDirectory, "OpenCueSpatial-v1.dll");
        var config = EngineService.Compile(profile, endpoint, dynamics, endpoints, spatial);
        if (profile.Enabled && profile.SpatialEnabled) RelayService.ValidateSpatialEndpoint(endpoint!.Id);
        if (profile.Enabled && !string.IsNullOrWhiteSpace(profile.Art16ProfilePath)) RelayService.ValidateArtEndpoint(endpoint!.Id);
        if (profile.Enabled && (profile.NeedsNative || profile.SpatialEnabled || !string.IsNullOrWhiteSpace(profile.Art16ProfilePath)))
        {
            if (!EngineService.IsX64Library(Path.Combine(engine.InstallPath, "EqualizerAPO.dll")))
                throw new IOException("This processing chain requires the x64 edition of Equalizer APO. The current root was left unchanged.");
            if (profile.NeedsNative) CheckNativePackage("OpenCue.Dynamics.dll", "OpenCueDynamics.dll", dynamics);
            if (profile.SpatialEnabled) CheckNativePackage("OpenCue.Spatial.dll", "OpenCueSpatial.dll", spatial);
        }

        var active = File.Exists(engine.ActiveFile) ? File.ReadAllBytes(engine.ActiveFile) : null;
        if (active is not null && !EngineService.Decode(active).Text.StartsWith(OwnedHeader, StringComparison.Ordinal))
            throw new IOException("The target active.txt belongs to another configuration. Whole-root takeover will not overwrite that file.");
        return new Prepared(config, File.ReadAllBytes(engine.RootFile), active,
            File.Exists(IntegrationPath(engine)) ? File.ReadAllBytes(IntegrationPath(engine)) : null);
    }

    private static void CheckNativePackage(string resourceName, string filename, string target)
    {
        byte[] bytes;
        var alongside = Path.Combine(AppContext.BaseDirectory, "engine", filename);
        if (File.Exists(alongside)) bytes = File.ReadAllBytes(alongside);
        else
        {
            using var resource = typeof(EngineService).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException("A required native audio processor is missing from this build. The current root was left unchanged.");
            using var output = new MemoryStream(); resource.CopyTo(output); bytes = output.ToArray();
        }
        using (var stream = new MemoryStream(bytes, false))
        using (var reader = new BinaryReader(stream))
        {
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("The bundled audio processor is not a valid native library.");
            stream.Position = 0x3c; var offset = reader.ReadInt32();
            if (offset < 64 || offset > stream.Length - 6) throw new InvalidDataException("The bundled audio processor has an invalid PE header.");
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
                throw new InvalidDataException("The bundled audio processor must be a Windows x64 library.");
        }
        if (File.Exists(target) && !Same(FileHash(target), Hash(bytes)))
            throw new IOException("A different audio processor occupies OpenCue's versioned path. The current root was left unchanged.");
    }

    private static string ApplyCore(EngineLocation engine, AudioProfile profile, RenderEndpoint? endpoint, Func<string> apply)
    {
        var prepared = Prepare(engine, profile, endpoint); // no writes before validation
        var directory = Path.Combine(engine.OwnDirectory, "recovery", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var record = new Recovery
        {
            RootPath = Path.GetFullPath(engine.RootFile), BackupPath = Path.Combine(directory, "root.before.bin"),
            RootBeforeHash = Hash(prepared.Root), RootLastHash = Hash(Encoding.UTF8.GetBytes(MinimalRoot)),
            IntermediateHash = Hash(Encoding.UTF8.GetBytes(MinimalRoot)), ExpectedRootHash = Hash(ManagedRoot()),
            ActiveExisted = prepared.Active is not null, ActiveBeforeHash = prepared.Active is null ? null : Hash(prepared.Active),
            ActiveLastHash = prepared.Active is null ? null : Hash(prepared.Active),
            CompiledActiveHash = Hash(new UTF8Encoding(false).GetBytes(prepared.Config)),
            IntegrationExisted = prepared.Integration is not null,
            IntegrationBeforeHash = prepared.Integration is null ? null : Hash(prepared.Integration)
        };
        WriteNew(record.BackupPath, prepared.Root);
        if (prepared.Active is not null) WriteNew(Path.Combine(directory, "active.before.bin"), prepared.Active);
        if (prepared.Integration is not null) WriteNew(Path.Combine(directory, "integration.before.bin"), prepared.Integration);
        if (File.Exists(PointerPath(engine))) WriteNew(Path.Combine(directory, "previous-takeover.json"), File.ReadAllBytes(PointerPath(engine)));
        Save(engine, record, directory);

        // Check every snapshot again before the first root mutation.
        if (!Same(FileHash(engine.RootFile), record.RootBeforeHash) || !Same(FileHash(engine.ActiveFile), record.ActiveBeforeHash)
            || !Same(FileHash(IntegrationPath(engine)), record.IntegrationBeforeHash))
        {
            record.Status = "CancelledByConflict"; Save(engine, record, directory);
            throw new IOException("The audio configuration changed during takeover preparation. Existing effects were left unchanged; a recovery copy was retained.");
        }

        record.Status = "Applying"; Save(engine, record, directory);
        try
        {
            EngineService.Atomic(engine.RootFile, Encoding.UTF8.GetBytes(MinimalRoot));
            var message = apply();
            if (!Same(FileHash(engine.RootFile), record.ExpectedRootHash))
                throw new IOException("The root changed unexpectedly while the new chain was being applied.");
            record.RootLastHash = record.ExpectedRootHash;
            record.ActiveLastHash = FileHash(engine.ActiveFile);
            record.IntegrationLastHash = FileHash(IntegrationPath(engine));
            record.Status = "Applied"; Save(engine, record, directory);
            return message + " Previous effects were replaced; exact recovery backup: " + record.BackupPath;
        }
        catch (Exception failure)
        {
            var root = FileHash(engine.RootFile);
            var safeRoot = Same(root, record.IntermediateHash) || Same(root, record.ExpectedRootHash);
            var safeActive = ActiveIsKnown(engine, record);
            try
            {
                if (safeRoot && safeActive)
                {
                    RestoreActive(engine, record, prepared.Active);
                    // Active restoration may take time; check the root again before replacing it.
                    if (!Same(FileHash(engine.RootFile), root)) throw new IOException("The root changed during rollback; the newer edit was preserved.");
                    EngineService.Atomic(engine.RootFile, prepared.Root);
                    RestoreIntegration(engine, record, prepared.Integration);
                    record.Status = "RolledBack";
                }
                else record.Status = "RecoveryRequired";
                Save(engine, record, directory);
            }
            catch (Exception rollback)
            {
                throw new IOException("Takeover failed and automatic rollback could not finish. External edits were not overwritten. Recovery backup: " + record.BackupPath,
                    new AggregateException(failure, rollback));
            }
            throw new IOException(record.Status == "RolledBack"
                ? "Takeover failed; the previous root and owned settings were restored. " + failure.Message
                : "Takeover failed; a newer/unknown edit was preserved. Recovery backup: " + record.BackupPath + ". " + failure.Message, failure);
        }
    }

    /// <summary>Call after a successful ordinary Apply or Bypass following takeover.</summary>
    public static void RecordManagedUpdate(EngineLocation engine)
    {
        var record = Load(engine);
        if (record is null || record.Status != "Applied" || !Same(FileHash(engine.RootFile), record.RootLastHash)) return;
        if (!File.Exists(engine.ActiveFile) || !EngineService.Decode(File.ReadAllBytes(engine.ActiveFile)).Text.StartsWith(OwnedHeader, StringComparison.Ordinal)) return;
        record.ActiveLastHash = FileHash(engine.ActiveFile);
        record.IntegrationLastHash = FileHash(IntegrationPath(engine));
        Save(engine, record, Path.GetDirectoryName(record.BackupPath)!);
    }

    public static bool CanRestore(EngineLocation engine) => Restorable(engine, out _, out _, out _, out _);

    /// <summary>False means preserve external edits and use ordinary surgical removal.</summary>
    public static bool TryRestore(EngineLocation engine)
    {
        if (!Restorable(engine, out var record, out var root, out var active, out var integration)) return false;
        RestoreActive(engine, record!, active);
        if (!Same(FileHash(engine.RootFile), record!.RootLastHash))
            throw new IOException("The root changed during recovery. Its newer contents were preserved; the previous backup remains available.");
        EngineService.Atomic(engine.RootFile, root!);
        RestoreIntegration(engine, record, integration);
        record.Status = "Restored";
        Save(engine, record, Path.GetDirectoryName(record.BackupPath)!);
        return true;
    }

    private static bool Restorable(EngineLocation engine, out Recovery? record, out byte[]? root, out byte[]? active, out byte[]? integration)
    {
        record = Load(engine); root = active = integration = null;
        if (record is null || record.Status != "Applied" || !Same(FileHash(engine.RootFile), record.RootLastHash) || !ActiveIsKnown(engine, record)) return false;
        var directory = Path.GetDirectoryName(record.BackupPath)!;
        if (!ReadVerified(record.BackupPath, record.RootBeforeHash, out root)) return false;
        if (record.ActiveExisted && !ReadVerified(Path.Combine(directory, "active.before.bin"), record.ActiveBeforeHash, out active)) return false;
        if (record.IntegrationExisted && !ReadVerified(Path.Combine(directory, "integration.before.bin"), record.IntegrationBeforeHash, out integration)) return false;
        return true;
    }

    private static bool ActiveIsKnown(EngineLocation engine, Recovery record)
    {
        var hash = FileHash(engine.ActiveFile);
        return Same(hash, record.ActiveBeforeHash) || Same(hash, record.ActiveLastHash)
            || Same(hash, record.CompiledActiveHash) || Same(hash, Hash(PlaceholderActive)) || Same(hash, Hash(StandardBypass));
    }

    private static void RestoreActive(EngineLocation engine, Recovery record, byte[]? original)
    {
        if (!ActiveIsKnown(engine, record)) throw new IOException("OpenCue's active file changed externally; it was left untouched.");
        if (original is not null) EngineService.Atomic(engine.ActiveFile, original);
        else if (File.Exists(engine.ActiveFile)) EngineService.Atomic(engine.ActiveFile, BypassedActive);
    }

    private static void RestoreIntegration(EngineLocation engine, Recovery record, byte[]? original)
    {
        var path = IntegrationPath(engine); var current = FileHash(path);
        // EngineService writes this exact metadata on the minimal root. Preserve other edits.
        var generated = Hash(Encoding.UTF8.GetBytes("{\"SeparatorAdded\":false}"));
        if (original is not null && (Same(current, record.IntegrationLastHash) || Same(current, generated) || Same(current, record.IntegrationBeforeHash)))
            EngineService.Atomic(path, original);
    }

    private static Recovery? Load(EngineLocation engine)
    {
        var pointer = PointerPath(engine);
        if (!File.Exists(pointer)) return null;
        Recovery? record;
        try { record = JsonSerializer.Deserialize<Recovery>(File.ReadAllBytes(pointer)); }
        catch (JsonException) { return null; }
        if (record is null || record.Version != 1 || !Same(record.RootPath, Path.GetFullPath(engine.RootFile)) || string.IsNullOrEmpty(record.BackupPath)) return null;
        var recoveryRoot = Path.GetFullPath(Path.Combine(engine.OwnDirectory, "recovery")) + Path.DirectorySeparatorChar;
        string backup;
        try { backup = Path.GetFullPath(record.BackupPath); }
        catch (ArgumentException) { return null; }
        if (!backup.StartsWith(recoveryRoot, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(backup) != "root.before.bin") return null;
        // Records may only address our direct timestamp/GUID recovery folder, never
        // an arbitrary nested path or a junction/symbolic link to another location.
        var directory = Path.GetDirectoryName(backup)!;
        if (!Same(Path.GetDirectoryName(directory), recoveryRoot.TrimEnd(Path.DirectorySeparatorChar))) return null;
        foreach (var path in new[] { recoveryRoot.TrimEnd(Path.DirectorySeparatorChar), directory, backup })
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        record.BackupPath = backup;
        return record;
    }

    private static bool ReadVerified(string path, string? expectedHash, out byte[]? bytes)
    {
        bytes = null;
        if (expectedHash is null || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        var data = File.ReadAllBytes(path);
        if (!Same(Hash(data), expectedHash)) return false;
        bytes = data; return true;
    }

    private static void Save(EngineLocation engine, Recovery record, string directory)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions { WriteIndented = true });
        EngineService.Atomic(Path.Combine(directory, "takeover.json"), bytes);
        EngineService.Atomic(PointerPath(engine), bytes);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }

    /// <summary>Scratch-only recovery tests. Creates a new unique subdirectory; deletes nothing.</summary>
    public static void SelfTest(string scratch)
    {
        var directory = Path.Combine(Path.GetFullPath(scratch), "takeover-" + Guid.NewGuid().ToString("N"));
        var engine = new EngineLocation(Path.Combine(directory, "install"), Path.Combine(directory, "config"));
        Directory.CreateDirectory(engine.OwnDirectory); Directory.CreateDirectory(engine.InstallPath);
        var before = new UnicodeEncoding(false, true).GetPreamble().Concat(new UnicodeEncoding(false, false).GetBytes("# Previous effects\r\nStage: pre-mix\r\nIf: true\r\nPreamp: -4 dB\r\nEndIf:\r\n")).ToArray();
        var active = Encoding.UTF8.GetBytes(OwnedHeader + "\r\n# previous owned preset\r\n");
        var profile = new AudioProfile { Enabled = false, LimiterEnabled = false };
        File.WriteAllBytes(engine.RootFile, before); File.WriteAllBytes(engine.ActiveFile, active);
        Apply(engine, profile, null);
        if (!CanRestore(engine) || !TryRestore(engine) || !before.SequenceEqual(File.ReadAllBytes(engine.RootFile)) || !active.SequenceEqual(File.ReadAllBytes(engine.ActiveFile)))
            throw new InvalidOperationException("Takeover test failed: exact UTF-16/stage/conditional root and active recovery.");
        Apply(engine, profile, null);
        File.AppendAllText(engine.RootFile, "# external edit\r\n");
        var edited = File.ReadAllBytes(engine.RootFile);
        if (CanRestore(engine) || TryRestore(engine) || !edited.SequenceEqual(File.ReadAllBytes(engine.RootFile)))
            throw new InvalidOperationException("Takeover test failed: external root edit preservation.");
        File.WriteAllBytes(engine.RootFile, before); File.WriteAllBytes(engine.ActiveFile, active);
        try { ApplyCore(engine, profile, null, () => { EngineService.Apply(engine, profile, null); throw new IOException("Injected apply failure"); }); }
        catch (IOException) { }
        if (!before.SequenceEqual(File.ReadAllBytes(engine.RootFile)) || !active.SequenceEqual(File.ReadAllBytes(engine.ActiveFile)))
            throw new InvalidOperationException("Takeover test failed: safe rollback after apply failure.");
        var invalid = profile.Copy(); invalid.Gains = [];
        try { Apply(engine, invalid, null); throw new InvalidOperationException("Invalid profile was accepted."); }
        catch (InvalidDataException) { }
        if (!before.SequenceEqual(File.ReadAllBytes(engine.RootFile))) throw new InvalidOperationException("Takeover test failed: prevalidation changed the root.");
    }
}
