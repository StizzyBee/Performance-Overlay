using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerformanceOverlay;

public enum OverlayOrientation { Horizontal, Vertical }

/// <summary>How a single chosen sensor is rendered. Label can be left blank to auto-generate.</summary>
public sealed class SensorChoice
{
    public string Id { get; set; } = "";
    /// <summary>Optional custom label. If empty, derived from the sensor (e.g. "GPU").</summary>
    public string CustomLabel { get; set; } = "";
    public bool ShowGraph { get; set; } = true;
}

/// <summary>
/// All user-configurable overlay settings. Serialized to
/// %AppData%\PerformanceOverlay\settings.json.
/// </summary>
public sealed class AppSettings
{
    public List<SensorChoice> Sensors { get; set; } = new();

    public int UpdateIntervalMs { get; set; } = 1000;
    public bool UseFahrenheit { get; set; } = false;

    // ---- FPS ----
    public bool EnableFpsMonitor { get; set; } = true;
    /// <summary>Rolling window (seconds) used for avg / min / max / 1% / 0.1% lows.</summary>
    public int FpsWindowSeconds { get; set; } = 30;
    /// <summary>Inject a hook DLL into the foreground game for precise frametimes + Vulkan/OpenGL. No-anti-cheat games only.</summary>
    public bool UseInjection { get; set; } = false;
    /// <summary>Manual FPS correction (frames) applied to the non-injection (ETW) reading. ETW counts
    /// "presented" frames, which can run above a vsync/displayed cap; set e.g. -10 to match an in-game counter.</summary>
    public int FpsOffset { get; set; } = 0;
    /// <summary>Show a dedicated per-frame frametime graph (frame pacing / stutter) in the overlay.</summary>
    public bool ShowFrametimeGraph { get; set; } = false;
    /// <summary>Show every sensor in the picker, including noisy ones (network adapters, GPU engine sub-loads). Off = curated list.</summary>
    public bool ShowAdvancedSensors { get; set; } = false;
    /// <summary>Limit the game's FPS to <see cref="FpsCap"/>. Requires DLL injection (the limiter lives in the hook).</summary>
    public bool FpsCapEnabled { get; set; } = false;
    public int FpsCap { get; set; } = 60;

    public OverlayOrientation Orientation { get; set; } = OverlayOrientation.Horizontal;
    public bool ShowLabels { get; set; } = true;
    public bool ShowUnits { get; set; } = true;
    public bool ShowGraphs { get; set; } = true;
    public int GraphSeconds { get; set; } = 60;

    // Defaults snapshotted from the user's preferred config so a fresh install matches it 1:1.
    public double FontSize { get; set; } = 14;
    /// <summary>ARGB hex, e.g. "#FFFFFFFF".</summary>
    public string TextColor { get; set; } = "#FFFFFFFF";
    public string AccentColor { get; set; } = "#FFFFFFFF"; // graph line (defaults to white)
    public string BackgroundColor { get; set; } = "#CC000000";
    public double BackgroundOpacity { get; set; } = 0.8;
    public string FontFamily { get; set; } = "Consolas";

    public double PositionX { get; set; } = 40;
    public double PositionY { get; set; } = 40;
    public bool LockPosition { get; set; } = false;
    public bool ClickThrough { get; set; } = true;
    public bool OverlayVisible { get; set; } = true;

    /// <summary>Modifier+key for the global show/hide hotkey (defaults to Ctrl+Shift+O).</summary>
    public string HotkeyModifiers { get; set; } = "Control,Shift";
    public string HotkeyKey { get; set; } = "O";

    // ---- persistence ----

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            var path = ConfigPaths.SettingsFile;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigPaths.SettingsDirectory);
            File.WriteAllText(ConfigPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* ignore disk errors */ }
    }
}

/// <summary>
/// Resolves where settings live. Portable by default: a "config" folder next to the
/// executable. The user can point this elsewhere; the chosen folder is remembered in a
/// tiny "config-location.txt" beside the exe (so it survives without needing settings).
/// </summary>
public static class ConfigPaths
{
    // The directory of the actual exe. NOTE: use Environment.ProcessPath, NOT
    // AppContext.BaseDirectory — with single-file self-extract the latter points at a temp
    // extraction folder, which would move the portable config away from the exe.
    public static string AppDir =>
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } d ? d : AppContext.BaseDirectory;
    public static string PortableDir => Path.Combine(AppDir, "config");
    private static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerformanceOverlay");
    // Pointer lives in LocalAppData so it's writable even if the app folder isn't.
    private static string PointerFile => Path.Combine(LocalRoot, "config-location.txt");

    private static string? _resolved;

    /// <summary>
    /// Current settings directory. Order: user-chosen folder (pointer) → portable folder
    /// next to the exe if writable → %LocalAppData%\PerformanceOverlay fallback.
    /// </summary>
    public static string SettingsDirectory
    {
        get
        {
            if (_resolved != null) return _resolved;
            try
            {
                if (File.Exists(PointerFile))
                {
                    var dir = File.ReadAllText(PointerFile).Trim();
                    if (!string.IsNullOrWhiteSpace(dir)) return _resolved = dir;
                }
            }
            catch { }
            return _resolved = CanWrite(PortableDir) ? PortableDir : LocalRoot;
        }
    }

    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Friendly label for the UI.</summary>
    public static bool IsPortable => IsSamePath(SettingsDirectory, PortableDir);

    /// <summary>Move config to a new directory (or back to the default when dir is null/empty).</summary>
    public static void SetLocation(string? newDir, AppSettings current)
    {
        try
        {
            Directory.CreateDirectory(LocalRoot);
            if (string.IsNullOrWhiteSpace(newDir))
            { if (File.Exists(PointerFile)) File.Delete(PointerFile); }
            else
                File.WriteAllText(PointerFile, newDir);

            _resolved = null; // force re-resolve
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        catch { }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static bool IsSamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
