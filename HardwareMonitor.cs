using LibreHardwareMonitor.Hardware;

namespace PerformanceOverlay;

/// <summary>
/// A single sensor reading exposed to the UI. The <see cref="Id"/> (LibreHardwareMonitor
/// identifier, e.g. "/intelcpu/0/temperature/0") is stable across runs and is what we
/// persist in settings to remember which sensors the user picked.
/// </summary>
public enum GraphRange { Auto, Percent, Temperature, Fps, Frametime }

public sealed class SensorInfo
{
    public required string Id { get; init; }
    public required string HardwareName { get; init; }
    public required HardwareType HardwareType { get; init; }
    public required string SensorName { get; init; }
    public required SensorType SensorType { get; init; }
    public float? Value { get; set; }

    // ---- overrides used by synthetic metrics (e.g. FPS) ----
    public string? UnitOverride { get; init; }
    public string? CategoryOverride { get; init; }
    public GraphRange Range { get; init; } = GraphRange.Auto;
    public int Decimals { get; init; } = -1; // -1 = auto by type

    /// <summary>Unit string for this sensor type, e.g. "%", "°C", "MHz".</summary>
    public string Unit => UnitOverride ?? UnitFor(SensorType);

    /// <summary>A short, friendly category label, e.g. "GPU", "CPU", "RAM".</summary>
    public string ShortCategory => CategoryOverride ?? CategoryFor(HardwareType);

    /// <summary>Auto-generated short label used when the user hasn't set a custom one.</summary>
    /// <summary>
    /// Compact label shown in the overlay (e.g. "GPU", "CPU", "VRAM", "FPS"). Multiple
    /// sensors sharing this label group into one segment in the overlay
    /// (e.g. "GPU 40% 34°"). Intentionally drops "Usage" / "Temp" so the overlay stays
    /// compact and the values speak for themselves via the unit (% vs °).
    /// </summary>
    public string OverlayLabel()
    {
        if (CategoryOverride == "FPS")
            return SensorName.Equals("FPS", StringComparison.OrdinalIgnoreCase) ? "FPS" : SensorName;

        string cat = ShortCategory;
        string name = SensorName;

        if (Generic(name)) return cat;

        // Synthetic sensors like "VRAM Usage" — in the overlay we want just the category.
        if (name.StartsWith(cat, StringComparison.OrdinalIgnoreCase)) return cat;

        return $"{cat} {name}";
    }

    /// <summary>
    /// Verbose label for the dashboard editor / active list. Adds "Usage" / "Temp" so
    /// the user can tell two sensors of the same category apart while editing.
    /// </summary>
    public string DescriptiveLabel()
    {
        if (CategoryOverride == "FPS")
            return SensorName.Equals("FPS", StringComparison.OrdinalIgnoreCase) ? "FPS" : SensorName;

        string cat = ShortCategory;
        string name = SensorName;

        if (Generic(name))
        {
            return SensorType switch
            {
                SensorType.Load or SensorType.Level or SensorType.Control => $"{cat} Usage",
                SensorType.Temperature => $"{cat} Temp",
                _ => cat,
            };
        }

        if (name.StartsWith(cat, StringComparison.OrdinalIgnoreCase)) return name;

        return $"{cat} {name}";
    }

    private static bool Generic(string name) =>
        name.Contains("Total", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Package", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Core", StringComparison.OrdinalIgnoreCase)
        || name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Memory", StringComparison.OrdinalIgnoreCase);

    public static string UnitFor(SensorType t) => t switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "MB/s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.Conductivity => "µS/cm",
        SensorType.Humidity => "%",
        _ => ""
    };

    public static string CategoryFor(HardwareType t) => t switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia => "GPU",
        HardwareType.GpuAmd => "GPU",
        HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "RAM",
        HardwareType.Motherboard => "MOBO",
        HardwareType.SuperIO => "MOBO",
        HardwareType.Storage => "DISK",
        HardwareType.Network => "NET",
        HardwareType.Cooler => "FAN",
        HardwareType.EmbeddedController => "EC",
        HardwareType.Psu => "PSU",
        HardwareType.Battery => "BATT",
        _ => t.ToString().ToUpperInvariant()
    };
}

/// <summary>
/// Wraps LibreHardwareMonitor. Enumerates every sensor the machine exposes and refreshes
/// their values on demand. Thread-affinity: create and use from the UI thread; Refresh()
/// is cheap enough to call on a DispatcherTimer.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Dictionary<string, SensorInfo> _byId = new();
    private bool _disposed;

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            // Network monitoring enumerates every adapter/VPN/filter driver — heavy to poll
            // and pure noise in a game overlay, so it's off (also declutters the picker).
            IsNetworkEnabled = false,
            IsControllerEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
        };
        _computer.Open();
        Rescan();
    }

    /// <summary>Every sensor currently known, in a stable order grouped by hardware.</summary>
    public IReadOnlyCollection<SensorInfo> AllSensors => _byId.Values;

    public SensorInfo? Get(string id) => _byId.TryGetValue(id, out var s) ? s : null;

    /// <summary>Network monitoring is off by default (heavy + noisy); turn it on when the
    /// user wants "show all sensors". Re-scans so the adapters appear/disappear.</summary>
    private readonly object _sync = new();

    public void SetNetworkEnabled(bool on)
    {
        lock (_sync)
        {
            if (_disposed || _computer.IsNetworkEnabled == on) return;
            _computer.IsNetworkEnabled = on;
            RescanLocked();
        }
    }

    public bool IsNetworkSensor(string id) =>
        _byId.TryGetValue(id, out var s) && s.HardwareType == HardwareType.Network;

    /// <summary>Re-walk the hardware tree to discover sensors (call once at startup).</summary>
    public void Rescan()
    {
        lock (_sync) RescanLocked();
    }

    private void RescanLocked()
    {
        _byId.Clear();
        foreach (var hw in _computer.Hardware)
            WalkHardware(hw);
        DetectSynthetic();
    }

    // ---- synthetic metrics derived from raw sensors (e.g. VRAM used in GB) ----
    private string? _vramUsedSrcId;
    private SensorInfo? _vramUsed;

    private void DetectSynthetic()
    {
        _vramUsedSrcId = null;
        _vramUsed = null;

        bool IsGpu(HardwareType t) => t is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        var used = _byId.Values.FirstOrDefault(s =>
            IsGpu(s.HardwareType) && s.SensorType == SensorType.SmallData &&
            s.SensorName.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) &&
            !s.SensorName.Contains("D3D", StringComparison.OrdinalIgnoreCase) &&
            !s.SensorName.Contains("Shared", StringComparison.OrdinalIgnoreCase));

        if (used != null)
        {
            _vramUsedSrcId = used.Id;
            _vramUsed = new SensorInfo
            {
                Id = "gpu:vram-used",
                HardwareName = used.HardwareName,
                HardwareType = used.HardwareType,
                SensorName = "VRAM Usage",
                SensorType = SensorType.SmallData,
                CategoryOverride = "VRAM",
                UnitOverride = "GB",
                Range = GraphRange.Auto,
                Decimals = 1,
            };
            _byId[_vramUsed.Id] = _vramUsed;
        }
    }

    private void UpdateSynthetic()
    {
        if (_vramUsed != null && _vramUsedSrcId != null &&
            _byId.TryGetValue(_vramUsedSrcId, out var src) && src.Value.HasValue)
            _vramUsed.Value = src.Value.Value / 1024f; // MB -> GB
    }

    private void WalkHardware(IHardware hw)
    {
        hw.Update();
        foreach (var sensor in hw.Sensors)
        {
            var id = sensor.Identifier.ToString();
            if (_byId.ContainsKey(id)) continue;
            _byId[id] = new SensorInfo
            {
                Id = id,
                HardwareName = hw.Name,
                HardwareType = hw.HardwareType,
                SensorName = sensor.Name,
                SensorType = sensor.SensorType,
                Value = sensor.Value,
                Range = sensor.SensorType switch
                {
                    SensorType.Load or SensorType.Level or SensorType.Control or SensorType.Humidity => GraphRange.Percent,
                    SensorType.Temperature => GraphRange.Temperature,
                    _ => GraphRange.Auto,
                },
            };
        }
        foreach (var sub in hw.SubHardware)
            WalkHardware(sub);
    }

    /// <summary>Refresh values for all hardware and push them into the SensorInfo objects.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        lock (_sync)
        {
            foreach (var hw in _computer.Hardware)
                RefreshHardware(hw);
            UpdateSynthetic();
        }
    }

    private void RefreshHardware(IHardware hw)
    {
        hw.Update();
        foreach (var sensor in hw.Sensors)
        {
            if (_byId.TryGetValue(sensor.Identifier.ToString(), out var info))
                info.Value = sensor.Value;
        }
        foreach (var sub in hw.SubHardware)
            RefreshHardware(sub);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _computer.Close(); } catch { /* ignore */ }
    }
}
