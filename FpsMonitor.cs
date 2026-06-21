using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using LibreHardwareMonitor.Hardware;

namespace PerformanceOverlay;

/// <summary>
/// Computed frame-rate statistics for the foreground game, derived from ETW present
/// events (the same data source PresentMon uses) — no injection, no anti-cheat risk.
///
/// Covers Direct3D 9/10/11/12 titles (DXGI / D3D9 present events). Pure Vulkan/OpenGL
/// games present outside DXGI and are not captured by this version. With DLSS/FSR Frame
/// Generation enabled, presented frames include the generated ones, so the reported FPS
/// is the "with frame-gen" number.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    // ---- metric ids (used as synthetic sensor ids in the overlay/dashboard) ----
    public const string IdCurrent = "fps:current";
    public const string IdAvg = "fps:avg";
    public const string IdMin = "fps:min";   // lowest
    public const string IdMax = "fps:max";   // highest
    public const string IdLow1 = "fps:low1";  // 1% low
    public const string IdLow01 = "fps:low01"; // 0.1% low
    public const string IdFrametime = "fps:frametime"; // ms

    private TraceEventSession? _session;
    private Thread? _pump;
    private volatile bool _running;

    private readonly object _lock = new();
    // pid -> list of recent present timestamps (ms, session-relative)
    private readonly Dictionary<int, List<double>> _presents = new();

    private int _targetPid;
    private double _windowSeconds = 30;

    // Latest computed values (read by the UI thread).
    public double? Current { get; private set; }
    public double? Average { get; private set; }
    public double? Min { get; private set; }
    public double? Max { get; private set; }
    public double? Low1 { get; private set; }
    public double? Low01 { get; private set; }
    public double? Frametime { get; private set; }

    /// <summary>True once the ETW session is up and listening.</summary>
    public bool Active { get; private set; }
    public string? Status { get; private set; }

    public void SetWindowSeconds(double seconds) => _windowSeconds = Math.Clamp(seconds, 1, 600);

    /// <summary>Manual FPS correction applied to ETW framerate readings only (injection is exact).</summary>
    public int Offset { get; set; }

    /// <summary>Active injected FPS cap (0 = none). Framerate readings are clamped to this so
    /// per-second sampling can't show an impossible above-cap value.</summary>
    public int ActiveCap { get; set; }

    /// <summary>Most recent per-frame frametimes (ms), oldest→newest, for the frametime graph.
    /// Snapshot reference is swapped atomically so the UI thread can read without locking.</summary>
    public double[] FrametimeSnapshot { get; private set; } = Array.Empty<double>();
    private const int FtSnapshotMax = 300;

    public void Start()
    {
        if (_running) return;
        try
        {
            _session = new TraceEventSession("PerfOverlay_FpsSession")
            {
                StopOnDispose = true,
            };

            // DXGI present events cover D3D10/11/12; D3D9 covers legacy DX9.
            _session.EnableProvider("Microsoft-Windows-DXGI", TraceEventLevel.Informational, ulong.MaxValue);
            try { _session.EnableProvider("Microsoft-Windows-D3D9", TraceEventLevel.Informational, ulong.MaxValue); }
            catch { /* optional */ }

            _session.Source.Dynamic.All += OnEvent;

            _running = true;
            Active = true;
            Status = "listening";
            _pump = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Status = "stopped: " + ex.Message; Active = false; }
            })
            { IsBackground = true, Name = "PerfOverlay-ETW" };
            _pump.Start();
        }
        catch (Exception ex)
        {
            Active = false;
            Status = "unavailable: " + ex.Message + " (run as Administrator)";
        }
    }

    /// <summary>The foreground game PID we care about; set each tick. 0 = ignore everything
    /// (cheap early-out so the ETW callback does almost no work for other processes).</summary>
    private volatile int _etwTargetPid;
    public void SetEtwTarget(int pid) => _etwTargetPid = pid;

    private void OnEvent(TraceEvent ev)
    {
        // Cheapest possible early-out first: only the foreground game's presents matter.
        // ProcessID is a cheap field; EventName below is a manifest lookup, so skipping
        // every other process here saves the bulk of the ETW CPU cost.
        int pid = ev.ProcessID;
        if (pid <= 0 || pid != _etwTargetPid) return;

        // Count one present per frame. DXGI/D3D9 emit a present "Start" event per frame
        // (and a matching "Stop"); count the Start and ignore the Stop to avoid doubling.
        var name = ev.EventName;
        if (name == null) return;
        if (name.IndexOf("Present", StringComparison.OrdinalIgnoreCase) < 0) return;
        if (name.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0) return;
        bool looksStart = ev.Opcode == (TraceEventOpcode)1
                          || name.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0
                          || name.Equals("Present", StringComparison.OrdinalIgnoreCase);
        if (!looksStart) return;

        double ts = ev.TimeStampRelativeMSec;

        lock (_lock)
        {
            if (!_presents.TryGetValue(pid, out var list))
            {
                list = new List<double>(256);
                _presents[pid] = list;
            }
            // DXGI emits paired present events per frame (Present + IDXGISwapChain_Present),
            // microseconds apart. Collapse anything within 0.3ms into a single frame so we
            // don't double-count. Real frames are >=1ms apart even at 1000 fps.
            if (list.Count > 0 && ts - list[^1] < 0.3) return;
            list.Add(ts);
        }
    }

    // ---- injection feed ----
    private readonly List<double> _injected = new();
    private bool _injectionEnabled;
    private long _lastInjectFeedTick;

    /// <summary>"Injection" when injected data is currently driving the stats, else "etw".</summary>
    public string ActiveSource { get; private set; } = "etw";
    public bool InjectionActive { get; private set; }

    public void SetInjectionEnabled(bool on)
    {
        _injectionEnabled = on;
        if (!on) { _injected.Clear(); InjectionActive = false; }
    }

    /// <summary>Feed present timestamps (ms, monotonic) captured by the injected hook.</summary>
    public void FeedInjected(IReadOnlyList<double> tsMs)
    {
        if (!_injectionEnabled || tsMs.Count == 0) return;
        // Belt-and-suspenders dedup: drop any timestamp within 0.05ms of the previous one
        // (the DLL already dedups, but guards against any double Present/Present1 capture).
        foreach (var t in tsMs)
        {
            if (_injected.Count > 0 && t - _injected[^1] < 0.05) continue;
            _injected.Add(t);
        }
        _lastInjectFeedTick = Environment.TickCount64;
        if (_injected.Count > 5000) _injected.RemoveRange(0, _injected.Count - 5000);
    }

    /// <summary>Recompute metrics for the current foreground process. Call from the UI tick.</summary>
    public void Update()
    {
        // Tell the ETW callback which PID to record (only the foreground game). When injection
        // is driving, we don't need ETW at all → 0 = record nothing.
        int fg = GetForegroundPid();
        _etwTargetPid = _injectionEnabled ? 0 : fg;

        // Prefer injected data when it's fresh (received within the last 2s).
        bool injFresh = _injectionEnabled && _injected.Count >= 3
                        && Environment.TickCount64 - _lastInjectFeedTick < 2000;
        if (injFresh)
        {
            PruneWindow(_injected);
            if (_injected.Count >= 3 && ComputeFrom(_injected))
            {
                InjectionActive = true;
                ActiveSource = "injection";
                return;
            }
        }
        InjectionActive = false;
        ActiveSource = "etw";

        if (!_running) { ClearMetrics(); return; }

        int pid = GetForegroundPid();
        if (pid != 0) _targetPid = pid;

        List<double>? frames = null;
        lock (_lock)
        {
            if (_targetPid != 0 && _presents.TryGetValue(_targetPid, out var list) && list.Count > 0)
            {
                PruneWindow(list);
                frames = new List<double>(list);
            }
            if (_presents.Count > 8)
            {
                foreach (var key in _presents.Keys.Where(k => k != _targetPid).ToList())
                    _presents.Remove(key);
            }
        }

        if (frames == null || !ComputeFrom(frames)) ClearMetrics();
    }

    private void PruneWindow(List<double> list)
    {
        if (list.Count == 0) return;
        double cutoff = list[^1] - _windowSeconds * 1000.0;
        int firstKeep = 0;
        while (firstKeep < list.Count && list[firstKeep] < cutoff) firstKeep++;
        if (firstKeep > 0) list.RemoveRange(0, firstKeep);
    }

    /// <summary>Compute all FPS metrics from a list of present timestamps (ms). Returns false if not enough data.</summary>
    private bool ComputeFrom(List<double> frames)
    {
        if (frames.Count < 3) return false;

        var dt = new List<double>(frames.Count - 1);
        for (int i = 1; i < frames.Count; i++)
        {
            double d = frames[i] - frames[i - 1];
            if (d > 0 && d < 10000) dt.Add(d);
        }
        if (dt.Count < 2) return false;

        // Current FPS: frames presented over the last ~1 second, the way RTSS / Steam report
        // it (count over actual elapsed time). More stable and reference-accurate than 1/dt.
        double recentCut = frames[^1] - 1000.0;
        int startIdx = 0;
        for (int i = frames.Count - 1; i >= 0; i--)
            if (frames[i] < recentCut) { startIdx = i + 1; break; }
        int recentCount = frames.Count - startIdx;
        if (recentCount >= 2)
        {
            double recentSpan = frames[^1] - frames[startIdx];
            Current = recentSpan > 0 ? (recentCount - 1) * 1000.0 / recentSpan : 1000.0 / dt[^1];
        }
        else Current = 1000.0 / dt[^1];

        double avgDt = dt.Average();
        Average = avgDt > 0 ? 1000.0 / avgDt : null;

        Max = 1000.0 / dt.Min();
        Min = 1000.0 / dt.Max();

        var sorted = dt.OrderBy(x => x).ToList();
        Low1 = 1000.0 / Percentile(sorted, 0.99);
        Low01 = 1000.0 / Percentile(sorted, 0.999);

        Frametime = dt[^1];

        // publish the most recent frametimes for the frametime graph
        int take = Math.Min(dt.Count, FtSnapshotMax);
        FrametimeSnapshot = dt.GetRange(dt.Count - take, take).ToArray();
        return true;
    }

    private static double Percentile(List<double> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return double.NaN;
        int idx = (int)Math.Ceiling(p * sortedAsc.Count) - 1;
        idx = Math.Clamp(idx, 0, sortedAsc.Count - 1);
        return sortedAsc[idx];
    }

    private void ClearMetrics()
    {
        Current = Average = Min = Max = Low1 = Low01 = Frametime = null;
        FrametimeSnapshot = Array.Empty<double>();
    }

    public float? ValueFor(string id)
    {
        var raw = id switch
        {
            IdCurrent => ToF(Current),
            IdAvg => ToF(Average),
            IdMin => ToF(Min),
            IdMax => ToF(Max),
            IdLow1 => ToF(Low1),
            IdLow01 => ToF(Low01),
            IdFrametime => ToF(Frametime),
            _ => (float?)null,
        };
        // Calibration: ETW counts presented frames (can exceed a vsync cap); the user can dial
        // an offset to match an in-game counter. Injection is exact, so it's left alone.
        if (raw.HasValue && id != IdFrametime && Offset != 0 && ActiveSource == "etw")
            raw = Math.Max(0, raw.Value + Offset);

        // FPS cap: the limiter holds an exact rate, so readings near the cap are sampling noise.
        // Clamp to <= cap and snap anything within ~1.5 fps of the cap to the exact cap value,
        // so a 144 cap reads a steady 144 (not 143.9). Genuine dips below stay real.
        if (raw.HasValue && ActiveCap > 0)
        {
            if (id == IdFrametime)
            {
                float floor = 1000f / ActiveCap;
                raw = Math.Max(raw.Value, floor);
                if (raw.Value <= floor * 1.04f) raw = floor; // at the cap → exact cap frametime
            }
            else
            {
                raw = Math.Min(raw.Value, ActiveCap);
                if (raw.Value >= ActiveCap - 1.5f) raw = ActiveCap; // at the cap → exact cap fps
            }
        }
        return raw;
    }

    private static float? ToF(double? d) => d.HasValue && !double.IsNaN(d.Value) && !double.IsInfinity(d.Value) ? (float)d.Value : null;

    /// <summary>The synthetic "sensors" this monitor exposes, for the dashboard picker.</summary>
    public static IEnumerable<SensorInfo> Metrics()
    {
        SensorInfo M(string id, string name, string unit, GraphRange range, int dec) => new()
        {
            Id = id,
            HardwareName = "Frames",
            HardwareType = HardwareType.Cpu, // unused; category overridden
            SensorName = name,
            SensorType = SensorType.Factor,
            CategoryOverride = "FPS",
            UnitOverride = unit,
            Range = range,
            Decimals = dec,
        };

        yield return M(IdCurrent, "FPS", "", GraphRange.Fps, 0);
        yield return M(IdAvg, "Average", "", GraphRange.Fps, 0);
        yield return M(IdMin, "Lowest", "", GraphRange.Fps, 0);
        yield return M(IdMax, "Highest", "", GraphRange.Fps, 0);
        yield return M(IdLow1, "1% Low", "", GraphRange.Fps, 0);
        yield return M(IdLow01, "0.1% Low", "", GraphRange.Fps, 0);
        yield return M(IdFrametime, "Frametime", "ms", GraphRange.Frametime, 1);
    }

    // ---- foreground process detection ----

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    private static int _ownPid = Process.GetCurrentProcess().Id;

    private static int GetForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == _ownPid) return 0; // ignore our own dashboard/overlay
        return pid;
    }

    public void Dispose()
    {
        _running = false;
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}
