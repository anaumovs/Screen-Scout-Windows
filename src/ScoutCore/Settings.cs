using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScoutCore;

[JsonConverter(typeof(JsonStringEnumConverter<ClickButton>))]
public enum ClickButton { Left, Right }

public sealed record ClickSettings
{
    public double MinInterval { get; set; } = 1.4;
    public double MaxInterval { get; set; } = 2.8;
    public double MovementProbability { get; set; } = .15;
    public double MovementRadius { get; set; } = 4;
    public double BreakProbability { get; set; } = .08;
    public double MinBreak { get; set; } = 5;
    public double MaxBreak { get; set; } = 10;
    public ClickButton Button { get; set; } = ClickButton.Right;
    // Windows virtual-key codes. Ctrl+Alt replaces macOS Control+Option.
    public int EmergencyCode { get; set; } = 0x77;
    public bool EmergencyControlAlt { get; set; }
    public bool RegularClickingEnabled { get; set; } = true;
    public bool SecondaryClickingEnabled { get; set; }
    public double SecondaryMinWait { get; set; } = 5;
    public double SecondaryMaxWait { get; set; } = 10;
    public bool RandomIntervalsEnabled { get; set; } = true;
    public bool SmallMovementEnabled { get; set; } = true;
    public bool LargeMovementEnabled { get; set; }
    public double LargeMovementProbability { get; set; } = .15;
    public double LargeMovementSpeed { get; set; } = 1;
    public bool SmallClickWhileMoving { get; set; }
    public bool LargeClickWhileMoving { get; set; }
    public string? MovementDisplayIdentity { get; set; }
    public List<RegionProfile> MovementRegions { get; set; } = [];
    public bool BreaksEnabled { get; set; } = true;
    public bool ClickVariationEnabled { get; set; }
    public double ClickVariationRadius { get; set; } = 2;
    public string MovementDisplayId { get; set; } = "";
    public bool TimerEnabled { get; set; }
    public int TimerHours { get; set; }
    public int TimerMinutes { get; set; } = 30;
    public int TimerSeconds { get; set; }
    public bool FinalClickEnabled { get; set; }
    public ClickButton FinalButton { get; set; } = ClickButton.Right;
    [JsonIgnore] public double BasicInterval => (MinInterval + MaxInterval) / 2;
    [JsonIgnore] public double TimerDuration => TimerHours * 3600.0 + TimerMinutes * 60 + TimerSeconds;
    [JsonIgnore] public bool FinalClickEffective => TimerEnabled && FinalClickEnabled;
    [JsonIgnore] public bool MovementEnabled => SmallMovementEnabled || LargeMovementEnabled;
    [JsonIgnore] public string? ValidationError
    {
        get
        {
            if (!double.IsFinite(SecondaryMinWait) || !double.IsFinite(SecondaryMaxWait) || SecondaryMinWait < .2 || SecondaryMaxWait < SecondaryMinWait || SecondaryMaxWait > 3600) return "Secondary waits must be finite, 0.2–3600 seconds, with maximum at least minimum.";
            if (!new[] { MinInterval, MaxInterval, MovementProbability, MovementRadius, BreakProbability, MinBreak, MaxBreak, ClickVariationRadius, LargeMovementProbability, LargeMovementSpeed }.All(double.IsFinite)) return "Use finite numbers for all settings.";
            if (MinInterval < .08 || MaxInterval < MinInterval || MaxInterval > 3600) return "Click intervals must be 0.08–3600 seconds, with maximum at least minimum.";
            if (new[] { MovementProbability, LargeMovementProbability, BreakProbability }.Any(x => x < 0 || x > 1)) return "Frequencies must be between 0 and 100%.";
            if (LargeMovementSpeed is < 1 or > 10) return "Large-movement speed must be 1×–10×.";
            if (MovementRadius is < 0 or > 50) return "Movement distance must be 0–50 screen pixels.";
            if (ClickVariationRadius is < 0 or > 10) return "Click variation must be 0–10 screen pixels.";
            if (TimerHours is < 0 or > 999 || TimerMinutes is < 0 or > 59 || TimerSeconds is < 0 or > 59 || (TimerEnabled && TimerDuration <= 0)) return "Set a positive timer: hours 0–999, minutes and seconds 0–59.";
            if (MinBreak < .2 || MaxBreak < MinBreak || MaxBreak > 3600) return "Breaks must be 0.2–3600 seconds, with maximum at least minimum.";
            if (EmergencyCode is not (0x77 or 0x78 or 0x79 or 0x1B)) return "Choose F8, F9, F10 or Escape for emergency stop.";
            if (!Enum.IsDefined(Button) || !Enum.IsDefined(FinalButton)) return "Choose a valid mouse button.";
            if (MovementRegions is null || MovementRegions.Any(p => p is null || p.Boxes is null || p.Boxes.Any(b => b is null))) return "Movement region data is invalid.";
            return null;
        }
    }

    public void SetBasicInterval(double value)
    {
        if (!double.IsFinite(value)) return;
        var center = Math.Clamp(value, .08, 3600);
        var spread = Math.Max(0, Math.Min((MaxInterval - MinInterval) / 2, Math.Min(center - .08, 3600 - center)));
        MinInterval = center - spread; MaxInterval = center + spread;
    }
    public ClickSettings Snapshot() => this with { MovementRegions = MovementRegions.Select(p => p with { Boxes = p.Boxes.Select(b => b with { }).ToList() }).ToList() };
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static ClickSettings Load(string? json)
    {
        try
        {
            if (json is null) return new();
            var settings = JsonSerializer.Deserialize<ClickSettings>(json, JsonOptions) ?? new();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("finalButton", out _)) settings.FinalButton = settings.Button;
            if (!root.TryGetProperty("smallMovementEnabled", out _) && root.TryGetProperty("movementEnabled", out var enabled))
            {
                var large = root.TryGetProperty("movementMode", out var mode) && mode.GetString() == "Full-screen random movement";
                settings.SmallMovementEnabled = enabled.GetBoolean() && !large;
                settings.LargeMovementEnabled = enabled.GetBoolean() && large;
            }
            if (!root.TryGetProperty("largeMovementProbability", out _)) settings.LargeMovementProbability = settings.MovementProbability;
            return settings.ValidationError is null ? settings : new();
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { return new(); }
    }
}

/// Listen-only chord latch; all chord keys must be released before rearming.
public sealed class ChordLatch(IEnumerable<int> keys)
{
    public HashSet<int> Keys { get; } = keys.ToHashSet();
    public HashSet<int> Pressed { get; } = [];
    public bool Latched { get; private set; }
    public bool Armed { get; private set; } = true;
    public bool Update(int code, bool down, bool repeated = false)
    {
        if (!Keys.Contains(code)) return false;
        if (down) Pressed.Add(code); else Pressed.Remove(code);
        if (Pressed.Count == 0) { Latched = false; Armed = true; }
        if (!down || repeated || !Armed || Latched || !Pressed.SetEquals(Keys)) return false;
        Latched = true; return true;
    }
    public void RequireRelease(IEnumerable<int>? down = null)
    {
        var currentlyDown = (down ?? []).Where(Keys.Contains).ToArray();
        Pressed.Clear(); Pressed.UnionWith(currentlyDown); Latched = false; Armed = Pressed.Count == 0;
    }
}
