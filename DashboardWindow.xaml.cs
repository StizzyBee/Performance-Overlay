using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace PerformanceOverlay;

public partial class DashboardWindow : Window
{
    private readonly ObservableCollection<SensorRow> _rows = new();
    private readonly ObservableCollection<ShownItem> _shown = new();
    private ICollectionView? _view;
    private bool _loading = true;
    private bool _suppress;

    private string? _selectedSensorId;

    public DashboardWindow()
    {
        InitializeComponent();
        BuildAvailable();
        BuildShownFromSettings();
        BuildFpsQuickAdd();
        PopulateFonts();
        LoadSettingsIntoControls();

        // wire up the editable overlay (the "Your Overlay" area in Sensors tab)
        ShownOverlay.EditMode = true;
        ShownOverlay.RequestRemove += OnShownRemove;
        ShownOverlay.RequestReorder += OnShownReorder;
        ShownOverlay.RequestSelect += OnShownSelect;
        ActiveList.ItemsSource = _shown;

        _loading = false;
        RebuildPreview();
        RebuildShownOverlay();
    }

    private void RebuildShownOverlay()
    {
        ShownOverlay.ApplySettings(App.Current.Settings);
        UpdateSelectionEditor();
    }

    private void OnShownRemove(string id)
    {
        RemoveShownById(id);
        _suppress = true;
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (row != null) row.IsSelected = false;
        _suppress = false;
        if (_selectedSensorId == id) _selectedSensorId = null;
        RebuildSettingsFromShown();
    }

    private void OnShownReorder(string fromId, string toId)
    {
        int from = _shown.IndexOf(_shown.FirstOrDefault(s => s.Id == fromId)!);
        int to = _shown.IndexOf(_shown.FirstOrDefault(s => s.Id == toId)!);
        if (from < 0 || to < 0 || from == to) return;
        _shown.Move(from, to);
        RebuildSettingsFromShown();
    }

    private void OnShownSelect(string id)
    {
        _selectedSensorId = id;
        UpdateSelectionEditor();
    }

    private void UpdateSelectionEditor()
    {
        var item = _selectedSensorId != null ? _shown.FirstOrDefault(s => s.Id == _selectedSensorId) : null;
        bool any = item != null;
        SelLabelBox.IsEnabled = SelGraphChk.IsEnabled = SelRemoveBtn.IsEnabled = any;
        if (!any)
        {
            SelLabel.Text = "Click a value above to edit it";
            SelLabelBox.Text = "";
            SelGraphChk.IsChecked = false;
            return;
        }
        SelLabel.Text = item!.Descriptive + ":";
        _loading = true;
        SelLabelBox.Text = item.Label;
        SelGraphChk.IsChecked = item.ShowGraph;
        _loading = false;
    }

    private void SelLabel_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || _selectedSensorId == null) return;
        var item = _shown.FirstOrDefault(s => s.Id == _selectedSensorId);
        if (item == null) return;
        item.SetLabelFromUser(SelLabelBox.Text);
        RebuildSettingsFromShown();
    }

    private void SelGraph_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _selectedSensorId == null) return;
        var item = _shown.FirstOrDefault(s => s.Id == _selectedSensorId);
        if (item == null) return;
        item.ShowGraph = SelGraphChk.IsChecked == true;
        RebuildSettingsFromShown();
    }

    private void SelRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSensorId != null) OnShownRemove(_selectedSensorId);
    }

    // ===================== available list =====================

    private void BuildAvailable()
    {
        _rows.Clear();
        var selected = App.Current.Settings.Sensors.Select(c => c.Id).ToHashSet();
        bool advanced = App.Current.Settings.ShowAdvancedSensors;

        var ordered = App.Current.AllMetrics()
            .OrderBy(s => CategoryRank(s.ShortCategory))
            .ThenBy(s => s.ShortCategory)
            .ThenBy(s => s.HardwareName)
            .ThenBy(s => s.SensorType.ToString())
            .ThenBy(s => s.SensorName);

        foreach (var info in ordered)
        {
            // Hide noisy/rarely-useful sensors unless "show all" is on — but always keep
            // anything the user has already selected so they can still see/remove it.
            if (!advanced && IsNoise(info) && !selected.Contains(info.Id)) continue;
            _rows.Add(new SensorRow(info) { IsSelected = selected.Contains(info.Id) });
        }

        if (_view == null)
        {
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.GroupName)));
            _view.Filter = FilterRow;
            SensorList.ItemsSource = _view;
        }
        UpdateLiveValues();
    }

    /// <summary>Sensors that clutter the picker and almost no one wants in a game overlay.</summary>
    private static bool IsNoise(SensorInfo s)
    {
        // Every network adapter / VPN / filter driver (the big offender)
        if (s.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Network) return true;
        // GPU per-engine D3D loads (D3D Copy, D3D Compute, D3D Video Decode, …)
        if (s.ShortCategory == "GPU" && s.SensorName.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int CategoryRank(string c) => c switch
    {
        "FPS" => 0, "GPU" => 1, "VRAM" => 2, "CPU" => 3, "RAM" => 4, _ => 9
    };

    private bool FilterRow(object o)
    {
        if (o is not SensorRow r) return false;
        var q = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private async void AdvancedSensors_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = AdvancedSensorsChk.IsChecked == true;
        App.Current.Settings.ShowAdvancedSensors = on;
        App.Current.Settings.Save();

        // Enabling network enumerates ~37 adapters — do it off the UI thread so the toggle
        // doesn't freeze. Keep network on if a NET sensor is actually in use.
        bool keepNet = !on && App.Current.Settings.Sensors.Any(c => App.Current.Monitor.IsNetworkSensor(c.Id));
        AdvancedSensorsChk.IsEnabled = false;
        StatusText.Text = on ? "Loading all sensors…" : "Updating…";
        try
        {
            await Task.Run(() =>
            {
                if (on) App.Current.Monitor.SetNetworkEnabled(true);
                else if (!keepNet) App.Current.Monitor.SetNetworkEnabled(false);
            });
            BuildAvailable();
            _view?.Refresh();
        }
        finally
        {
            AdvancedSensorsChk.IsEnabled = true;
            StatusText.Text = on ? "Showing all sensors" : "Ready";
        }
    }

    // ----- FPS cap -----
    private void FpsCap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        CommitFpsCap();
    }

    private void FpsCapSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _loading = true; FpsCapBox.Text = ((int)FpsCapSlider.Value).ToString(); _loading = false;
        CommitFpsCap();
    }

    private void FpsCapBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        int v = ParseCap();
        _loading = true; FpsCapBox.Text = v.ToString(); FpsCapSlider.Value = Math.Clamp(v, 20, 360); _loading = false;
        CommitFpsCap();
    }

    private void FpsCapBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FpsCapBox_Changed(sender, e); Keyboard.ClearFocus(); }
    }

    private int ParseCap()
        => int.TryParse(FpsCapBox.Text, out int v) ? Math.Clamp(v, 10, 1000) : (int)FpsCapSlider.Value;

    /// <summary>Lightweight commit — the App tick reads FpsCap/FpsCapEnabled live, so no overlay rebuild needed.</summary>
    private void CommitFpsCap()
    {
        var s = App.Current.Settings;
        s.FpsCapEnabled = FpsCapChk.IsChecked == true;
        s.FpsCap = ParseCap();
        s.Save();
        UpdateFpsCapEnabledState();
        StatusText.Text = "Saved  •  " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void UpdateFpsCapEnabledState()
    {
        bool inj = App.Current.Settings.UseInjection;
        bool capOn = FpsCapChk.IsChecked == true;
        FpsCapSlider.IsEnabled = FpsCapBox.IsEnabled = capOn;
        if (!inj)
        {
            FpsCapNote.Text = "⚠ Requires DLL injection (above) to be enabled.";
            FpsCapNote.Foreground = (Brush)FindResource("DangerBrush");
        }
        else
        {
            FpsCapNote.Text = capOn ? "Active while a game is injected." : "Tick to enable.";
            FpsCapNote.Foreground = (Brush)FindResource("TextDimBrush");
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        // While searching, auto-expand the categories so matches are visible; collapse again
        // when the search is cleared.
        bool expand = !string.IsNullOrWhiteSpace(SearchBox.Text);
        Dispatcher.BeginInvoke(new Action(() => SetAllCategoriesExpanded(expand)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SetAllCategoriesExpanded(bool expanded)
    {
        foreach (var ex in FindVisualChildren<Expander>(SensorList))
            ex.IsExpanded = expanded;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindVisualChildren<T>(child)) yield return d;
        }
    }

    private void Sensor_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _suppress) return;
        if (sender is not CheckBox cb || cb.DataContext is not SensorRow row) return;

        if (row.IsSelected)
        {
            if (_shown.All(s => s.Id != row.Id))
            {
                var info = App.Current.Metric(row.Id);
                if (info != null) _shown.Add(ShownItem.From(info, null, true));
            }
        }
        else
        {
            RemoveShownById(row.Id);
        }
        RebuildSettingsFromShown();
    }

    // ===================== shown / ordered list =====================

    private void BuildShownFromSettings()
    {
        _shown.Clear();
        foreach (var c in App.Current.Settings.Sensors)
        {
            var info = App.Current.Metric(c.Id);
            if (info == null) continue;
            _shown.Add(ShownItem.From(info, string.IsNullOrEmpty(c.CustomLabel) ? null : c.CustomLabel, c.ShowGraph));
        }
    }

    private void RemoveShownById(string id)
    {
        var item = _shown.FirstOrDefault(s => s.Id == id);
        if (item != null) _shown.Remove(item);
    }

    private void RebuildSettingsFromShown()
    {
        var s = App.Current.Settings;
        s.Sensors = _shown.Select(i => new SensorChoice
        {
            Id = i.Id,
            CustomLabel = i.IsCustomLabel ? i.Label : "",
            ShowGraph = i.ShowGraph,
        }).ToList();
        Persist();
        RebuildPreview();
        RebuildShownOverlay();
        UpdateSelectionEditor();
    }

    // ===================== FPS quick-add =====================

    private void BuildFpsQuickAdd()
    {
        FpsMetricsPanel.Children.Clear();
        foreach (var m in FpsMonitor.Metrics())
        {
            var cb = new CheckBox
            {
                Content = $"{m.SensorName}{(string.IsNullOrEmpty(m.Unit) ? "" : $"  ({m.Unit})")}",
                Tag = m.Id,
                IsChecked = App.Current.Settings.Sensors.Any(c => c.Id == m.Id),
            };
            cb.Checked += FpsQuick_Toggled;
            cb.Unchecked += FpsQuick_Toggled;
            FpsMetricsPanel.Children.Add(cb);
        }
    }

    private void FpsQuick_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _suppress) return;
        if (sender is not CheckBox { Tag: string id } cb) return;
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (cb.IsChecked == true)
        {
            if (_shown.All(s => s.Id != id))
            {
                var info = App.Current.Metric(id);
                if (info != null) _shown.Add(ShownItem.From(info, null, true));
            }
        }
        else RemoveShownById(id);

        _suppress = true;
        if (row != null) row.IsSelected = cb.IsChecked == true;
        _suppress = false;
        RebuildSettingsFromShown();
    }

    // ===================== controls <-> settings =====================

    private void PopulateFonts()
    {
        var families = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s).ToList();
        foreach (var fav in new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "Segoe UI" }.Reverse())
            if (families.Remove(fav)) families.Insert(0, fav);
        FontBox.ItemsSource = families;
    }

    private void LoadSettingsIntoControls()
    {
        var s = App.Current.Settings;
        OrientationBox.SelectedIndex = s.Orientation == OverlayOrientation.Horizontal ? 0 : 1;
        ShowLabelsChk.IsChecked = s.ShowLabels;
        ShowGraphsChk.IsChecked = s.ShowGraphs;
        FahrenheitChk.IsChecked = s.UseFahrenheit;
        LockChk.IsChecked = s.LockPosition;
        ClickThroughChk.IsChecked = s.ClickThrough;
        EnableFpsChk.IsChecked = s.EnableFpsMonitor;
        InjectionChk.IsChecked = s.UseInjection;
        FrametimeGraphChk.IsChecked = s.ShowFrametimeGraph;
        AdvancedSensorsChk.IsChecked = s.ShowAdvancedSensors;
        FpsCapChk.IsChecked = s.FpsCapEnabled;
        FpsCapSlider.Value = Math.Clamp(s.FpsCap, 20, 360);
        FpsCapBox.Text = s.FpsCap.ToString();
        UpdateFpsCapEnabledState();

        FontBox.SelectedItem = s.FontFamily;
        if (FontBox.SelectedItem == null && FontBox.Items.Count > 0) FontBox.SelectedIndex = 0;

        FontSizeSlider.Value = s.FontSize;
        FpsWindowSlider.Value = s.FpsWindowSeconds;
        // Show 0 while injecting (calibration doesn't apply); slider is disabled. The
        // saved value in settings is preserved and re-shown when injection turns off.
        FpsOffsetSlider.Value = s.UseInjection ? 0 : s.FpsOffset;
        FpsOffsetSlider.IsEnabled = !s.UseInjection;

        TextColorBox.Text = s.TextColor;
        BackColorBox.Text = s.BackgroundColor;
        OpacitySlider.Value = AlphaPercent(s.BackgroundColor);

        UpdateDerivedTexts();
        UpdateSwatches();
        RefreshStatusTexts();
    }

    private void UpdateDerivedTexts()
    {
        FontSizeText.Text = ((int)FontSizeSlider.Value).ToString();
        FpsWindowText.Text = ((int)FpsWindowSlider.Value) + "s";
        int off = (int)FpsOffsetSlider.Value;
        FpsOffsetText.Text = (off > 0 ? "+" : "") + off + " fps";
        OpacityText.Text = ((int)OpacitySlider.Value) + "%";
    }

    private void RefreshStatusTexts()
    {
        var s = App.Current.Settings;
        HotkeyText.Text = $"Toggle: {s.HotkeyModifiers.Replace(",", "+")}+{s.HotkeyKey}";
        HotkeyText2.Text = HotkeyText.Text + "  •  Performance Overlay";
        ConfigPathText.Text = (ConfigPaths.IsPortable ? "Portable — " : "Custom — ") + ConfigPaths.SettingsFile;
        var fps = App.Current.Fps;
        string src = fps.ActiveSource == "injection" ? "  •  source: injection" : "";
        FpsStatusText.Text = s.EnableFpsMonitor
            ? (fps.Active ? "Status: listening for frames (open a game to see FPS)" + src : $"Status: {fps.Status ?? "starting…"}")
            : "Status: disabled" + src;

        var inj = App.Current.Injection;
        if (!s.UseInjection)
            InjectionStatusText.Text = "Off";
        else if (!inj.DllAvailable)
            InjectionStatusText.Text = "PerfOverlayHook.dll missing — rebuild the native project";
        else if (fps.InjectionActive)
            InjectionStatusText.Text = $"Active — capturing frames ({ApiNames(inj.LastApiMask)})";
        else
            InjectionStatusText.Text = inj.LastError != null
                ? $"Waiting… (last: {inj.LastError})"
                : "Enabled — focus a game (32 or 64-bit) to inject";
    }

    private static string ApiNames(uint mask)
    {
        var parts = new List<string>();
        if ((mask & 1) != 0) parts.Add("DirectX");
        if ((mask & 2) != 0) parts.Add("OpenGL");
        if ((mask & 4) != 0) parts.Add("Vulkan");
        return parts.Count > 0 ? string.Join(", ", parts) : "…";
    }

    private void AnyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ReadControlsIntoSettings();
        UpdateDerivedTexts();
        // Reflect the injection→calibration interlock immediately when injection toggles.
        _loading = true;
        FpsOffsetSlider.Value = App.Current.Settings.UseInjection ? 0 : App.Current.Settings.FpsOffset;
        FpsOffsetSlider.IsEnabled = !App.Current.Settings.UseInjection;
        _loading = false;
        UpdateFpsCapEnabledState(); // cap needs injection; refresh its note/enabled state
        Persist();
        RefreshStatusTexts();
        RebuildPreview();
        RebuildShownOverlay();
    }

    private void ReadControlsIntoSettings()
    {
        var s = App.Current.Settings;
        s.Orientation = OrientationBox.SelectedIndex == 1 ? OverlayOrientation.Vertical : OverlayOrientation.Horizontal;
        s.ShowLabels = ShowLabelsChk.IsChecked == true;
        s.ShowUnits = true; // always show units
        s.ShowGraphs = ShowGraphsChk.IsChecked == true;
        s.UseFahrenheit = FahrenheitChk.IsChecked == true;
        s.LockPosition = LockChk.IsChecked == true;
        s.ClickThrough = ClickThroughChk.IsChecked == true;
        s.EnableFpsMonitor = EnableFpsChk.IsChecked == true;
        s.UseInjection = InjectionChk.IsChecked == true;
        s.ShowFrametimeGraph = FrametimeGraphChk.IsChecked == true;

        if (FontBox.SelectedItem is string f) s.FontFamily = f;
        s.FontSize = FontSizeSlider.Value;
        s.FpsWindowSeconds = (int)FpsWindowSlider.Value;
        // Don't overwrite the user's calibration with the disabled slider's 0 while injecting.
        if (!s.UseInjection) s.FpsOffset = (int)FpsOffsetSlider.Value;

        s.TextColor = NormalizeHex(TextColorBox.Text, "#FFFFFFFF");
        s.AccentColor = s.TextColor; // graphs use the text color
        // background: rgb from box, alpha from opacity slider
        var rgb = ParseColor(BackColorBox.Text) ?? Color.FromRgb(0, 0, 0);
        byte a = (byte)Math.Round(OpacitySlider.Value / 100.0 * 255);
        s.BackgroundColor = $"#{a:X2}{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
        BackColorBox.Text = s.BackgroundColor;

        UpdateSwatches();
    }

    // ===================== color helpers =====================

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string which) return;
        var box = which == "text" ? TextColorBox : BackColorBox;
        var cur = ParseColor(box.Text) ?? Colors.White;

        using var dlg = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B),
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;

        var c = dlg.Color;
        if (which == "back")
            box.Text = $"#{(byte)Math.Round(OpacitySlider.Value / 100.0 * 255):X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        else
            box.Text = $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";

        AnyChanged(box, e);
    }

    private void UpdateSwatches()
    {
        TextColorSwatch.Background = SafeBrush(TextColorBox.Text);
        BackColorSwatch.Background = SafeBrush(BackColorBox.Text);
    }

    private static Color? ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim())!; }
        catch { return null; }
    }
    private static Brush SafeBrush(string hex)
    {
        var c = ParseColor(hex);
        return c.HasValue ? new SolidColorBrush(c.Value) : Brushes.Transparent;
    }
    private static string NormalizeHex(string hex, string fallback)
        => ParseColor(hex).HasValue ? hex.Trim() : fallback;
    private static double AlphaPercent(string hex)
    {
        var c = ParseColor(hex);
        return c.HasValue ? c.Value.A / 255.0 * 100 : 60;
    }

    // ===================== preview (renders the real OverlayView, 1:1) =====================

    /// <summary>Rebuild the preview from current settings (call on any settings/selection change).</summary>
    private void RebuildPreview() => PreviewView.ApplySettings(App.Current.Settings);

    // ===================== live values =====================

    // While the window is being moved/resized, skip the per-tick UI refresh so the drag
    // stays smooth (same idea as the overlay's drag fix).
    private bool _interacting;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        if (msg == WM_ENTERSIZEMOVE) _interacting = true;
        else if (msg == WM_EXITSIZEMOVE) { _interacting = false; UpdateLiveValues(); }
        return IntPtr.Zero;
    }

    public void UpdateLiveValues()
    {
        if (_interacting) return;
        bool f = App.Current.Settings.UseFahrenheit;
        foreach (var r in _rows) r.Refresh(f);
        foreach (var i in _shown) i.Refresh(f);
        if (!_loading) { PreviewView.UpdateReadings(); ShownOverlay.UpdateReadings(); RefreshStatusTexts(); }
    }

    // ===================== persistence / buttons =====================

    private void Persist()
    {
        App.Current.Settings.Save();
        App.Current.ApplySettings();
        StatusText.Text = "Saved  •  " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e) => App.Current.ToggleOverlay();

    private void SnapTL_Click(object sender, RoutedEventArgs e) => App.Current.SnapOverlay(0);
    private void SnapTR_Click(object sender, RoutedEventArgs e) => App.Current.SnapOverlay(1);
    private void SnapBL_Click(object sender, RoutedEventArgs e) => App.Current.SnapOverlay(2);
    private void SnapBR_Click(object sender, RoutedEventArgs e) => App.Current.SnapOverlay(3);

    private void Injection_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (InjectionChk.IsChecked == true)
        {
            // Hard confirmation: turning ON is the dangerous direction.
            var result = MessageBox.Show(this,
                "DLL injection loads a small DLL into the foreground game so the overlay can read its exact frame timings (and works for Vulkan / OpenGL games).\n\n" +
                "⚠ ONLY use this for games WITHOUT anti-cheat (no Valorant, Fortnite, EA AC, Easy Anti-Cheat, BattlEye, Vanguard, etc.). Injecting into an anti-cheat-protected game can get you banned and may crash the game.\n\n" +
                "For safety this turns itself OFF as soon as the injected game closes, so it never carries over.\n\n" +
                "Enable DLL injection?",
                "Enable DLL injection?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                _loading = true;
                InjectionChk.IsChecked = false;
                _loading = false;
                return;
            }
        }
        AnyChanged(sender, e);
    }

    /// <summary>Reflect an externally-changed injection setting (e.g. auto-off when a game closed).</summary>
    public void SyncInjectionState()
    {
        _loading = true;
        InjectionChk.IsChecked = App.Current.Settings.UseInjection;
        FpsOffsetSlider.Value = App.Current.Settings.UseInjection ? 0 : App.Current.Settings.FpsOffset;
        FpsOffsetSlider.IsEnabled = !App.Current.Settings.UseInjection;
        UpdateDerivedTexts();
        _loading = false;
        RefreshStatusTexts();
        if (!App.Current.Settings.UseInjection)
            StatusText.Text = "Injection auto-disabled (game closed)  •  " + DateTime.Now.ToString("HH:mm:ss");
    }

    // ---- social links ----
    private void Social_GitHub(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/StizzyBee");
    private void Social_CurseForge(object sender, RoutedEventArgs e) => OpenUrl("https://www.curseforge.com/members/stizzybee/projects");

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked */ }
    }

    // ---- custom title bar ----
    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ChangeConfig_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog { Description = "Choose where to store settings" };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        ConfigPaths.SetLocation(dlg.SelectedPath, App.Current.Settings);
        RefreshStatusTexts();
        StatusText.Text = "Config moved  •  " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void PortableConfig_Click(object sender, RoutedEventArgs e)
    {
        ConfigPaths.SetLocation(null, App.Current.Settings);
        RefreshStatusTexts();
        StatusText.Text = "Config set to app folder  •  " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reset all overlay settings to defaults? Your sensor selection is kept.",
            "Reset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var keep = App.Current.Settings.Sensors;
        App.Current.ReplaceSettings(new AppSettings { Sensors = keep });
        _loading = true;
        LoadSettingsIntoControls();
        _loading = false;
        Persist();
        RebuildPreview();
    }
}

// ===================== view models =====================

/// <summary>Row in the "Available" picker.</summary>
public sealed class SensorRow : INotifyPropertyChanged
{
    private readonly SensorInfo _info;
    public SensorRow(SensorInfo info)
    {
        _info = info;
        Name = info.CategoryOverride == "FPS" ? info.SensorName : $"{info.SensorName}  ·  {info.SensorType}";
        GroupName = $"{info.ShortCategory} — {info.HardwareName}";
    }

    public string Id => _info.Id;
    public string Name { get; }
    public string GroupName { get; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnChanged(nameof(IsSelected)); } }

    private string _liveValue = "—";
    public string LiveValue { get => _liveValue; private set { _liveValue = value; OnChanged(nameof(LiveValue)); } }

    public void Refresh(bool fahrenheit) => LiveValue = ShownItem.Format(_info, fahrenheit);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Row in the ordered "Shown in overlay" list.</summary>
public sealed class ShownItem : INotifyPropertyChanged
{
    private readonly SensorInfo _info;
    public string Id => _info.Id;
    public string Category => _info.ShortCategory;
    public string DefaultName => _info.OverlayLabel();
    public string Descriptive => _info.DescriptiveLabel();
    /// <summary>"GPU Core (Temperature)" — fuller description for the active list, vs the short overlay label.</summary>
    public string DisplayName => _info.CategoryOverride == "FPS"
        ? _info.SensorName
        : $"{_info.SensorName} ({_info.SensorType})";

    public bool IsCustomLabel { get; private set; }

    private string _label = "";
    public string Label { get => _label; set { _label = value; OnChanged(nameof(Label)); OnChanged(nameof(PreviewText)); } }

    private bool _showGraph = true;
    public bool ShowGraph { get => _showGraph; set { _showGraph = value; OnChanged(nameof(ShowGraph)); } }

    private string _liveValue = "—";
    public string LiveValue { get => _liveValue; private set { _liveValue = value; OnChanged(nameof(LiveValue)); OnChanged(nameof(PreviewText)); } }

    public string PreviewText => $"{Label} {ValueOnly}".Trim();
    public string ValueOnly => _liveValue == "—" ? SampleValue() : _liveValue;

    private ShownItem(SensorInfo info) => _info = info;

    public static ShownItem From(SensorInfo info, string? customLabel, bool showGraph)
    {
        var item = new ShownItem(info) { _showGraph = showGraph };
        if (!string.IsNullOrWhiteSpace(customLabel)) { item._label = customLabel; item.IsCustomLabel = true; }
        else item._label = info.OverlayLabel();
        return item;
    }

    public void SetLabelFromUser(string text)
    {
        _label = text;
        IsCustomLabel = !string.Equals(text, _info.OverlayLabel(), StringComparison.Ordinal);
    }

    public void Refresh(bool fahrenheit) => LiveValue = Format(_info, fahrenheit);

    private string SampleValue() => _info.Range switch
    {
        GraphRange.Percent => "50%",
        GraphRange.Temperature => "60°",
        GraphRange.Fps => "144",
        GraphRange.Frametime => "6.9ms",
        _ => "—",
    };

    /// <summary>Format a metric value for display (shared by both lists).</summary>
    public static string Format(SensorInfo info, bool fahrenheit)
    {
        var v = info.Value;
        if (v is null) return "—";
        double value = v.Value;
        string unit = info.Unit;
        if (info.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && info.UnitOverride == null && fahrenheit)
        { value = value * 9.0 / 5.0 + 32.0; unit = "°F"; }

        int dec = info.Decimals >= 0 ? info.Decimals
            : info.SensorType switch
            {
                LibreHardwareMonitor.Hardware.SensorType.Load or LibreHardwareMonitor.Hardware.SensorType.Level
                    or LibreHardwareMonitor.Hardware.SensorType.Control or LibreHardwareMonitor.Hardware.SensorType.Humidity => 0,
                LibreHardwareMonitor.Hardware.SensorType.Temperature or LibreHardwareMonitor.Hardware.SensorType.Clock => 0,
                _ => 1,
            };
        string fmt = "0" + (dec > 0 ? "." + new string('0', dec) : "");
        return $"{value.ToString(fmt)}{unit}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
