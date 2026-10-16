using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCue;

public sealed class AudioProfile
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Neutral";
    public bool Enabled { get; set; } = true;
    public double PreampDb { get; set; } = -6;
    public double[] Gains { get; set; } = new double[10];
    public bool DynamicsEnabled { get; set; }
    public double ThresholdDb { get; set; } = -12;
    public double Ratio { get; set; } = 3;
    public double CeilingDb { get; set; } = -3;
    public double AttackMs { get; set; } = 4;
    public double ReleaseMs { get; set; } = 100;
    public double MakeupDb { get; set; }
    public double TransientAmount { get; set; }
    public double AmbienceAmount { get; set; }
    public double FloorDb { get; set; } = -50;
    public double LevelingAmount { get; set; }
    public double Width { get; set; } = 1;
    public double Balance { get; set; }
    public bool LimiterEnabled { get; set; } = true;
    public bool SpatialEnabled { get; set; }
    public string? Art16ProfilePath { get; set; }
    public string? HeadphoneCorrection { get; set; }
    public string? CorrectionName { get; set; }
    [JsonIgnore] public CorrectionData? Correction=>string.IsNullOrWhiteSpace(HeadphoneCorrection)?null:CorrectionService.Parse(HeadphoneCorrection);
    [JsonIgnore] public bool NeedsNative => DynamicsEnabled || LimiterEnabled || MakeupDb != 0 || TransientAmount != 0 || AmbienceAmount != 0 || LevelingAmount != 0 || Width != 1 || Balance != 0;
    public string? DeviceId { get; set; }
    public bool AllDevices { get; set; }
    public static readonly double[] Frequencies = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public static readonly string[] Labels = ["31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public AudioProfile Copy() => JsonSerializer.Deserialize<AudioProfile>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("This profile version is not supported.");
        if (Gains is null || Gains.Length != 10 || Gains.Any(g => !double.IsFinite(g) || g < -12 || g > 6)) throw new InvalidDataException("A profile needs ten EQ gains between -12 and +6 dB.");
        if (!double.IsFinite(PreampDb) || PreampDb < -24 || PreampDb > 0) throw new InvalidDataException("Headroom must be between -24 and 0 dB.");
        if (!double.IsFinite(ThresholdDb) || ThresholdDb < -30 || ThresholdDb > 0 || !double.IsFinite(Ratio) || Ratio < 1 || Ratio > 8 || !double.IsFinite(CeilingDb) || CeilingDb < -12 || CeilingDb > -0.5) throw new InvalidDataException("Dynamics values are outside their supported ranges.");
        static bool Within(double value,double min,double max)=>double.IsFinite(value)&&value>=min&&value<=max;
        if(!Within(AttackMs,1,50)||!Within(ReleaseMs,20,500)||!Within(MakeupDb,0,12)||!Within(TransientAmount,0,1)||!Within(AmbienceAmount,0,1)||!Within(FloorDb,-80,-20)||!Within(LevelingAmount,0,1)||!Within(Width,0,2)||!Within(Balance,-1,1))throw new InvalidDataException("A fine control is outside its supported range.");
        if(SpatialEnabled&&AllDevices)throw new InvalidDataException("7.1 rendering must target one verified eight-channel virtual playback output.");
        if(!string.IsNullOrWhiteSpace(Art16ProfilePath)&&(AllDevices||SpatialEnabled))throw new InvalidDataException("Use one spatial engine on one virtual playback output. Turn off OpenCue 7.1 when selecting an official ArtTune 16ch profile.");
        _=Correction;
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new InvalidDataException("Use a profile name of 1–100 characters.");
    }
    [JsonIgnore] public double SafePreamp => -Math.Max(1, Gains.Where(g => g > 0).Sum() + (Correction?.PositiveGainBound??0) + 1 + (LimiterEnabled ? 0 : MakeupDb + 12*LevelingAmount + 20*Math.Log10(Math.Max(1,Width))));
    public static AudioProfile[] Presets =>
    [
        new() { Name = "Neutral", PreampDb = -6 },
        new() { Name = "Detail", PreampDb = -8, Gains = [-6,-4,-2,-1,0,1,2,2,0,-1] },
        new() { Name = "Less rumble", PreampDb = -6, Gains = [-9,-7,-4,-1,0,0,1,1,0,-1] },
        new() { Name = "Late night", PreampDb = -6, Gains = [-5,-3,-1,0,0,1,1,0,-1,-2], DynamicsEnabled = true, ThresholdDb = -18, Ratio = 4, CeilingDb = -6 }
    ];
    public static string Description(string name) => name switch
    {
        "Neutral" => "Flat EQ with some headroom. A useful starting point for any headphones or IEMs.",
        "Detail" => "Less low-end weight and a gentle presence lift. An original starting point, not a measured headset correction.",
        "Less rumble" => "A broad low-frequency cut for bass-heavy mixes. Other low sounds will also be reduced.",
        "Late night" => "Gentle EQ with compression and a lower peak ceiling. Adjust your listening volume separately.",
        _ => "Your settings. EQ and dynamics affect the whole stereo mix; they do not isolate individual game sounds."
    };
}

public sealed record RenderEndpoint(string Id, string Name, bool IsDefault = false)
{
    public override string ToString() => Name + (IsDefault ? "  ·  Windows default" : "");
    public string GuidSelector
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(Id, @"\{[0-9a-fA-F-]{36}\}$");
            if (!match.Success || !Guid.TryParse(match.Value, out _)) throw new InvalidDataException("The selected output has no valid Windows endpoint ID.");
            return match.Value;
        }
    }
}
