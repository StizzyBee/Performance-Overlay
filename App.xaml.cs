using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PerformanceOverlay;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public App()
    {
        // Single-file WPF can't satisfy WPF's load of Accessibility.dll for the MSAA->UIA
        // bridge when a ToolTip/ComboBox popup opens — it throws FileNotFoundException and
        // crashes. Resolve it from the self-extract directory ourselves.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (name is "Accessibility")
            {
                try
                {
                    var p = Path.Combine(AppContext.BaseDirectory, "Accessibility.dll");
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
                catch { }
            }
            return null;
        };

        // Safety net: never let a stray UI-thread exception hard-crash the whole overlay.
        DispatcherUnhandledException += (_, e) => e.Handled = true;
    }

    public AppSettings Settings { get; private set; } = null!;
    public HardwareMonitor Monitor { get; private set; } = null!;
    public FpsMonitor Fps { get; private set; } = null!;
    public InjectionManager Injection { get; private set; } = null!;

    private readonly Dictionary<string, SensorInfo> _fpsMetrics = new();

    /// <summary>Swap in a new settings object (used by "reset to defaults").</summary>
    public void ReplaceSettings(AppSettings s) => Settings = s;

    /// <summary>Look up any metric (hardware sensor OR fps metric) by id.</summary>
    public SensorInfo? Metric(string id) =>
        _fpsMetrics.TryGetValue(id, out var f) ? f : Monitor.Get(id);

    /// <summary>All selectable metrics: hardware sensors plus FPS metrics.</summary>
    public IEnumerable<SensorInfo> AllMetrics() =>
        _fpsMetrics.Values.Concat(Monitor.AllSensors);

    private OverlayWindow? _overlay;
    private DashboardWindow? _dashboard;
    private DispatcherTimer? _timer;
    private Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = AppSettings.Load();

        try
        {
            Monitor = new HardwareMonitor();
            // Network is off by default; re-enable if the user left "show all" on, or has a
            // network sensor selected, so those keep working across restarts.
            if (Settings.ShowAdvancedSensors || Settings.Sensors.Any(c => c.Id.StartsWith("/nic/", StringComparison.OrdinalIgnoreCase)))
                Monitor.SetNetworkEnabled(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start hardware monitoring.\n\n" + ex.Message +
                "\n\nMake sure the app is running as Administrator (temperatures need it).",
                "Performance Overlay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // FPS metrics (synthetic sensors) + ETW monitor
        Fps = new FpsMonitor();
        Injection = new InjectionManager();
        foreach (var m in FpsMonitor.Metrics()) _fpsMetrics[m.Id] = m;
        Fps.SetWindowSeconds(Settings.FpsWindowSeconds);
        if (Settings.EnableFpsMonitor) Fps.Start();

        if (Settings.Sensors.Count == 0)
            SeedDefaultSensors();

        _overlay = new OverlayWindow();
        _overlay.Show();
        _overlay.ApplySettings(Settings);
        _overlay.Visibility = Settings.OverlayVisible ? Visibility.Visible : Visibility.Hidden;

        SetupTray();
        StartTimer();

        ShowDashboard();
    }

    /// <summary>Pick common GPU/CPU/RAM load & temperature sensors so the overlay shows something useful immediately.</summary>
    private void SeedDefaultSensors()
    {
        var all = Monitor.AllSensors.ToList();
        var chosen = new List<SensorChoice>();

        void AddBest(Func<SensorInfo, bool> filter, bool graph, params string[] preferredNames)
        {
            var matches = all.Where(filter).ToList();
            if (matches.Count == 0) return;
            SensorInfo? pick = null;
            foreach (var name in preferredNames)
            {
                pick = matches.FirstOrDefault(m => m.SensorName.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (pick != null) break;
            }
            pick ??= matches[0];
            if (chosen.All(c => c.Id != pick.Id))
                chosen.Add(new SensorChoice { Id = pick.Id, ShowGraph = graph });
        }

        bool IsGpu(SensorInfo s) => s.ShortCategory == "GPU";
        bool IsCpu(SensorInfo s) => s.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu;
        bool IsRam(SensorInfo s) => s.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Memory;
        bool Load(SensorInfo s) => s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load;
        bool Temp(SensorInfo s) => s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature;

        // Defaults baked from the user's preferred config (1:1 on their hardware): FPS + GPU
        // usage/temp + VRAM usage + CPU usage/temp + RAM usage, graphs OFF except VRAM.
        chosen.Add(new SensorChoice { Id = FpsMonitor.IdCurrent, ShowGraph = false });
        AddBest(s => IsGpu(s) && Load(s), false, "GPU Core", "D3D", "Core");
        AddBest(s => IsGpu(s) && Temp(s), false, "GPU Core", "Core", "Hot Spot");
        if (Monitor.Get("gpu:vram-used") != null)
            chosen.Add(new SensorChoice { Id = "gpu:vram-used", ShowGraph = true });
        AddBest(s => IsCpu(s) && Load(s), false, "CPU Total", "Total");
        AddBest(s => IsCpu(s) && Temp(s), false, "Package", "Core Average", "Core (Tctl", "Tctl");
        AddBest(s => IsRam(s) && Load(s), false, "Memory");

        Settings.Sensors = chosen;
        Settings.Save();
    }

    private bool _refreshing;

    private void StartTimer()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(100, Settings.UpdateIntervalMs)) };
        _timer.Tick += (_, _) =>
        {
            // While the user is dragging the overlay, skip everything so the drag is buttery.
            if (_overlay?.IsDragging == true) return;
            // Idle: nothing is visible (overlay hidden AND dashboard closed) → skip the heavy
            // sensor/FPS work entirely; there's nothing to display.
            bool overlayShown = _overlay != null && _overlay.Visibility == Visibility.Visible;
            if (!overlayShown && _dashboard == null) { Fps.SetEtwTarget(0); return; }
            // Don't queue overlapping refreshes if the last one is still running.
            if (_refreshing) return;
            _refreshing = true;

            // Run the heavy work (sensor reads, ETW pruning, injection polling) on a thread-pool
            // thread so it never hitches the UI / drags / window resizes. When it's done we
            // marshal back to the UI thread to refresh the overlay + dashboard.
            _ = Task.Run(() =>
            {
                try { Monitor.Refresh(); } catch { }
                try
                {
                    Fps.SetInjectionEnabled(Settings.UseInjection);
                    Fps.Offset = Settings.FpsOffset;
                    Fps.ActiveCap = (Settings.UseInjection && Settings.FpsCapEnabled) ? Math.Max(10, Settings.FpsCap) : 0;
                    bool autoOff = false;
                    if (Settings.UseInjection)
                    {
                        if (Injection.CheckInjectedClosed()) autoOff = true;
                        else
                        {
                            int gpid = Injection.ForegroundGamePid();
                            if (gpid != 0)
                            {
                                Injection.EnsureInjected(gpid);
                                Fps.FeedInjected(Injection.Poll(gpid));
                                // push the FPS cap to the hook (0 = uncapped)
                                Injection.SetFpsCap(gpid, Settings.FpsCapEnabled ? Math.Max(10, Settings.FpsCap) : 0);
                            }
                        }
                    }
                    Fps.Update();
                    foreach (var m in _fpsMetrics.Values) m.Value = Fps.ValueFor(m.Id);

                    Dispatcher.Invoke(() =>
                    {
                        if (autoOff)
                        {
                            // SAFETY: injected game closed — turn injection off automatically so
                            // it can't carry over into the next game (possibly anti-cheat).
                            Settings.UseInjection = false;
                            Settings.Save();
                            Fps.SetInjectionEnabled(false);
                            Injection.Reset();
                            _dashboard?.SyncInjectionState();
                        }
                        _overlay?.UpdateReadings(Settings);
                        _dashboard?.UpdateLiveValues();
                    });
                }
                catch { /* ignore */ }
                finally { _refreshing = false; }
            });
        };
        _timer.Start();
    }

    /// <summary>Re-read settings everywhere after the dashboard changes them.</summary>
    public void ApplySettings()
    {
        Fps.SetWindowSeconds(Settings.FpsWindowSeconds);
        if (Settings.EnableFpsMonitor) Fps.Start(); // no-op if already running
        StartTimer();
        _overlay?.ApplySettings(Settings);
        _overlay!.Visibility = Settings.OverlayVisible ? Visibility.Visible : Visibility.Hidden;
        UpdateTrayMenu();
    }

    /// <summary>Snap the overlay to a screen corner (0=TL,1=TR,2=BL,3=BR). Recovers an off-screen overlay.</summary>
    public void SnapOverlay(int corner)
    {
        if (_overlay == null) return;
        if (!Settings.OverlayVisible) ToggleOverlay(); // make sure it's visible so the user sees it move
        var wa = SystemParameters.WorkArea; // primary screen, DIPs (matches WPF Left/Top)
        double w = _overlay.ActualWidth > 0 ? _overlay.ActualWidth : 220;
        double h = _overlay.ActualHeight > 0 ? _overlay.ActualHeight : 40;
        const double m = 16;
        double x = (corner == 1 || corner == 3) ? wa.Right - w - m : wa.Left + m;
        double y = (corner == 2 || corner == 3) ? wa.Bottom - h - m : wa.Top + m;
        Settings.PositionX = x;
        Settings.PositionY = y;
        Settings.Save();
        _overlay.Left = x;
        _overlay.Top = y;
    }

    public void ToggleOverlay()
    {
        Settings.OverlayVisible = !Settings.OverlayVisible;
        _overlay!.Visibility = Settings.OverlayVisible ? Visibility.Visible : Visibility.Hidden;
        Settings.Save();
        UpdateTrayMenu();
    }

    public void ShowDashboard()
    {
        if (_dashboard == null)
        {
            _dashboard = new DashboardWindow();
            _dashboard.Closed += (_, _) => _dashboard = null;
            _dashboard.Show();
        }
        else
        {
            _dashboard.Activate();
            _dashboard.WindowState = WindowState.Normal;
        }
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = IconFactory.CreateTrayIcon(32),
            Visible = true,
            Text = "Performance Overlay",
        };
        _tray.DoubleClick += (_, _) => ShowDashboard();
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_tray == null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add(Settings.OverlayVisible ? "Hide Overlay" : "Show Overlay", null, (_, _) => ToggleOverlay());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Fps?.Dispose();
        Injection?.Dispose();
        Monitor?.Dispose();
        Settings?.Save();
        base.OnExit(e);
    }
}
