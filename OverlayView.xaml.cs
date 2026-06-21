using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace PerformanceOverlay;

/// <summary>
/// The overlay's visual, shared by the live overlay window and the dashboard preview so
/// the preview is 1:1 with what the game shows. Renders selected metrics grouped into
/// labelled segments ("GPU 37% 58°") separated by "|", each value with an optional graph.
/// </summary>
public partial class OverlayView : UserControl
{
    private readonly List<OverlayGroup> _groups = new();
    public bool EditMode { get; set; }

    /// <summary>Raised when the user clicks the × on a value (edit mode).</summary>
    public event Action<string>? RequestRemove;
    /// <summary>Raised when the user reorders by drag (edit mode). Args: fromId, toId (insert before).</summary>
    public event Action<string, string>? RequestReorder;
    /// <summary>Raised when the user clicks a value to select it (edit mode).</summary>
    public event Action<string>? RequestSelect;

    public OverlayView()
    {
        InitializeComponent();
        ItemsHost.ItemsSource = _groups;
        AllowDrop = true;
    }

    // ----- edit-mode mouse handlers -----

    private Point _dragStart;
    private OverlayValue? _dragSource;

    private void Value_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!EditMode) return;
        if (sender is FrameworkElement { Tag: OverlayValue v }) v.IsHovered = true;
    }

    private void Value_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!EditMode) return;
        if (sender is FrameworkElement { Tag: OverlayValue v }) v.IsHovered = false;
    }

    private void Value_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!EditMode) return;
        if (sender is FrameworkElement { Tag: OverlayValue v })
        {
            _dragStart = e.GetPosition(null);
            _dragSource = v;
            RequestSelect?.Invoke(v.SensorId);
            v.IsSelected = true;
            foreach (var g in _groups) foreach (var other in g.Values) if (other != v) other.IsSelected = false;
        }
    }

    private void Value_MouseMove(object sender, MouseEventArgs e)
    {
        if (!EditMode || _dragSource == null || e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (sender is FrameworkElement fe)
        {
            var data = new DataObject("PerfOverlayValue", _dragSource.SensorId);
            DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
            _dragSource = null;
        }
    }

    private void Value_RemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OverlayValue v }) RequestRemove?.Invoke(v.SensorId);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (!EditMode) return;
        if (e.Data.GetData("PerfOverlayValue") is not string fromId) return;
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not FrameworkElement { Tag: OverlayValue }) hit = VisualTreeHelper.GetParent(hit);
        string toId = (hit as FrameworkElement)?.Tag is OverlayValue tv ? tv.SensorId : "";
        if (!string.IsNullOrEmpty(toId) && fromId != toId)
            RequestReorder?.Invoke(fromId, toId);
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        if (!EditMode) return;
        e.Effects = e.Data.GetDataPresent("PerfOverlayValue") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Rebuild from settings (selection, grouping, colors). Resets graph history.</summary>
    public void ApplySettings(AppSettings s)
    {
        RootBorder.Background = BrushFromHex(s.BackgroundColor, Color.FromArgb(0xCC, 0, 0, 0));

        var textBrush = BrushFromHex(s.TextColor, Colors.White);
        var accentBrush = BrushFromHex(s.AccentColor, Colors.White);
        var sepBrush = Dim(textBrush, 0.5);
        var family = SafeFamily(s.FontFamily);

        int maxPoints = Math.Max(2, s.GraphSeconds);
        double graphH = Math.Max(10, s.FontSize * 0.9);

        var values = new List<OverlayValue>();
        foreach (var choice in s.Sensors)
        {
            var info = App.Current.Metric(choice.Id);
            if (info == null) continue;
            values.Add(new OverlayValue(choice, info, textBrush, accentBrush, maxPoints, graphH, family, s.FontSize)
            {
                ShowUnits = s.ShowUnits,
                UseFahrenheit = s.UseFahrenheit,
                GraphEnabled = s.ShowGraphs && choice.ShowGraph,
                EditMode = EditMode,
            });
        }

        _groups.Clear();
        OverlayGroup? cur = null;
        foreach (var v in values)
        {
            if (cur == null || cur.Label != v.ResolvedLabel)
            {
                cur = new OverlayGroup
                {
                    Label = v.ResolvedLabel,
                    LabelBrush = textBrush,
                    SepBrush = sepBrush,
                    FontFamily = family,
                    FontSize = s.FontSize,
                    LabelVisibility = s.ShowLabels && !string.IsNullOrEmpty(v.ResolvedLabel)
                        ? Visibility.Visible : Visibility.Collapsed,
                };
                _groups.Add(cur);
            }
            cur.Values.Add(v);
        }

        bool horizontal = s.Orientation == OverlayOrientation.Horizontal;
        for (int i = 0; i < _groups.Count; i++)
            _groups[i].SeparatorVisibility = horizontal && i < _groups.Count - 1
                ? Visibility.Visible : Visibility.Collapsed;

        ItemsHost.ItemsSource = null;
        ItemsHost.ItemsSource = _groups;
        SetItemsPanel(s.Orientation);

        // ----- frametime graph -----
        _ftEnabled = s.ShowFrametimeGraph;
        Outer.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        FtPanel.Visibility = _ftEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (_ftEnabled)
        {
            FtSep.Foreground = sepBrush;
            FtSep.FontFamily = family; FtSep.FontSize = s.FontSize;
            FtSep.Visibility = horizontal && _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FtLabel.Foreground = textBrush;
            FtLabel.FontFamily = family; FtLabel.FontSize = s.FontSize;
            FtLabel.Visibility = s.ShowLabels ? Visibility.Visible : Visibility.Collapsed;
            FtLine.Stroke = accentBrush;
            double w = Math.Round(s.FontSize * 7.5);
            double h = Math.Round(s.FontSize * 2.2);
            FtGrid.Width = w; FtGrid.Height = h;
            FtPanel.Margin = horizontal ? new Thickness(0) : new Thickness(0, 4, 0, 0);
        }

        UpdateReadings();
    }

    private bool _ftEnabled;
    private const double FtScaleMs = 50.0; // fixed 0..50ms scale (16.7ms=60fps, 33.3ms=30fps)

    /// <summary>Push fresh sensor values into the existing items (called every tick).</summary>
    public void UpdateReadings()
    {
        foreach (var g in _groups)
            foreach (var v in g.Values)
                v.Update();

        if (_ftEnabled) DrawFrametimeGraph();
    }

    private void DrawFrametimeGraph()
    {
        double w = FtGrid.Width, h = FtGrid.Height;
        if (double.IsNaN(w) || w <= 0 || h <= 0) return;

        // 60 fps reference line
        double refY = h - (16.667 / FtScaleMs) * h;
        FtRef.X1 = 0; FtRef.X2 = w; FtRef.Y1 = refY; FtRef.Y2 = refY;

        var data = App.Current.Fps.FrametimeSnapshot;
        if (data.Length < 2) { FtLine.Points = new PointCollection(); return; }

        var pts = new PointCollection(data.Length);
        double step = w / (data.Length - 1);
        for (int i = 0; i < data.Length; i++)
        {
            double norm = Math.Clamp(data[i] / FtScaleMs, 0, 1);
            pts.Add(new Point(i * step, h - norm * h));
        }
        pts.Freeze();
        FtLine.Points = pts;
    }

    /// <summary>
    /// Choose the items-panel type from EditMode + orientation:
    /// - Real overlay (EditMode=false): StackPanel (horizontal or vertical) — never wraps.
    /// - Dashboard editor (EditMode=true): WrapPanel so values flow to the next row when
    ///   the dashboard column isn't wide enough to show them all.
    /// </summary>
    private void SetItemsPanel(OverlayOrientation o)
    {
        string xaml;
        if (EditMode)
        {
            // WrapPanel wraps in BOTH orientations (just along the orientation axis).
            string wpOrient = o == OverlayOrientation.Horizontal ? "Horizontal" : "Vertical";
            xaml = "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                   $"<WrapPanel Orientation='{wpOrient}'/></ItemsPanelTemplate>";
        }
        else
        {
            string spOrient = o == OverlayOrientation.Horizontal ? "Horizontal" : "Vertical";
            xaml = "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                   $"<StackPanel Orientation='{spOrient}'/></ItemsPanelTemplate>";
        }
        ItemsHost.ItemsPanel = (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static SolidColorBrush BrushFromHex(string hex, Color fallback)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); b.Freeze(); return b; }
        catch { var b = new SolidColorBrush(fallback); b.Freeze(); return b; }
    }

    private static SolidColorBrush Dim(SolidColorBrush b, double factor)
    {
        var c = b.Color;
        var d = new SolidColorBrush(Color.FromArgb((byte)(c.A * factor), c.R, c.G, c.B));
        d.Freeze();
        return d;
    }

    private static FontFamily SafeFamily(string name)
    {
        try { return new FontFamily(name); } catch { return new FontFamily("Consolas"); }
    }
}

/// <summary>A labelled segment of the overlay, e.g. "GPU 37% 58°".</summary>
public sealed class OverlayGroup
{
    public string Label { get; set; } = "";
    public Brush LabelBrush { get; set; } = Brushes.White;
    public Brush SepBrush { get; set; } = Brushes.Gray;
    public FontFamily FontFamily { get; set; } = new("Consolas");
    public double FontSize { get; set; } = 16;
    public Visibility LabelVisibility { get; set; } = Visibility.Visible;
    public Visibility SeparatorVisibility { get; set; } = Visibility.Collapsed;
    public List<OverlayValue> Values { get; } = new();
}

/// <summary>One metric's value (and graph) inside a group.</summary>
public sealed class OverlayValue : INotifyPropertyChanged
{
    private readonly SensorChoice _choice;
    private readonly SensorInfo _info;
    private readonly Queue<float> _history = new();
    private readonly int _maxPoints;

    public OverlayValue(SensorChoice choice, SensorInfo info, Brush text, Brush accent,
                        int maxPoints, double graphHeight, FontFamily family, double fontSize)
    {
        _choice = choice;
        _info = info;
        _maxPoints = maxPoints;
        TextBrush = text;
        AccentBrush = accent;
        GraphHeight = graphHeight;
        GraphWidth = 56;
        FontFamily = family;
        FontSize = fontSize;
    }

    public bool ShowUnits { get; init; } = true;
    public bool UseFahrenheit { get; init; }
    public bool GraphEnabled { get; init; } = true;
    public bool EditMode { get; init; }

    public string SensorId => _info.Id;

    public Brush TextBrush { get; }
    public Brush AccentBrush { get; }
    public double GraphHeight { get; }
    public double GraphWidth { get; }
    public FontFamily FontFamily { get; }
    public double FontSize { get; }

    public Visibility GraphVisibility => GraphEnabled ? Visibility.Visible : Visibility.Collapsed;

    public string ResolvedLabel => !string.IsNullOrWhiteSpace(_choice.CustomLabel)
        ? _choice.CustomLabel : _info.OverlayLabel();

    // ----- edit-mode chrome -----
    private bool _isHovered, _isSelected;
    public bool IsHovered { get => _isHovered; set { _isHovered = value; NotifyEditChanges(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; NotifyEditChanges(); } }
    public Brush EditOutlineBrush => !EditMode ? Brushes.Transparent
        : IsSelected ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))
        : IsHovered ? new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF))
        : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    public Brush EditBgBrush => EditMode && IsSelected
        ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
    public Visibility HoverVisibility => EditMode && (IsHovered || IsSelected) ? Visibility.Visible : Visibility.Collapsed;
    public Cursor EditCursor => EditMode ? Cursors.Hand : Cursors.Arrow;

    private void NotifyEditChanges()
    {
        OnChanged(nameof(EditOutlineBrush));
        OnChanged(nameof(EditBgBrush));
        OnChanged(nameof(HoverVisibility));
    }

    private string _valueText = "";
    public string ValueText { get => _valueText; private set { _valueText = value; OnChanged(nameof(ValueText)); } }

    private PointCollection _graphPoints = new();
    public PointCollection GraphPoints { get => _graphPoints; private set { _graphPoints = value; OnChanged(nameof(GraphPoints)); } }

    public void Update()
    {
        float? v = _info.Value;
        ValueText = BuildValue(v);

        if (GraphEnabled && v.HasValue)
        {
            _history.Enqueue(v.Value);
            while (_history.Count > _maxPoints) _history.Dequeue();
            GraphPoints = BuildGraph();
        }
    }

    private string BuildValue(float? v)
    {
        if (v is null) return "--";

        double value = v.Value;
        string unit = _info.Unit;
        if (_info.SensorType == SensorType.Temperature && _info.UnitOverride == null && UseFahrenheit)
        {
            value = value * 9.0 / 5.0 + 32.0;
            unit = "°F";
        }

        int decimals = _info.Decimals >= 0
            ? _info.Decimals
            : _info.SensorType switch
            {
                SensorType.Load or SensorType.Level or SensorType.Control or SensorType.Humidity => 0,
                SensorType.Temperature or SensorType.Clock => 0,
                SensorType.Data or SensorType.SmallData => 1,
                _ => 1,
            };

        string fmt = "0" + (decimals > 0 ? "." + new string('0', decimals) : "");
        string unitPart = ShowUnits && unit.Length > 0 ? unit : "";
        return $"{value.ToString(fmt)}{unitPart}";
    }

    private PointCollection BuildGraph()
    {
        var vals = _history.ToArray();
        if (vals.Length < 2) return new PointCollection();

        (double min, double max) = NormRange(vals);
        if (max - min < 1e-6) max = min + 1;

        var pts = new PointCollection(vals.Length);
        double step = GraphWidth / (vals.Length - 1);
        for (int i = 0; i < vals.Length; i++)
        {
            double norm = Math.Clamp((vals[i] - min) / (max - min), 0, 1);
            pts.Add(new Point(i * step, GraphHeight - norm * GraphHeight));
        }
        pts.Freeze();
        return pts;
    }

    private (double min, double max) NormRange(float[] values)
    {
        switch (_info.Range)
        {
            case GraphRange.Percent: return (0, 100);
            case GraphRange.Temperature: return (20, 100);
            case GraphRange.Fps:
            {
                double hi = 1;
                foreach (var v in values) hi = Math.Max(hi, v);
                return (0, hi);
            }
            default:
            {
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var v in values) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                return (lo, hi);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
