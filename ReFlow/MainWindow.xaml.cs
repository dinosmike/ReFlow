using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinFormsScreen = System.Windows.Forms.Screen;
using MessageBox = System.Windows.MessageBox;
using ReFlow.Models;
using ReFlow.Services;

namespace ReFlow;

public partial class MainWindow : INotifyPropertyChanged
{
    /// <summary>Заливка под графиком: <c>#RRGGBB</c> или <c>#AARRGGBB</c> (как в XAML).</summary>
    private const string MiniGraphFillColorHex = "#4020D9B8";

    private const double MiniGraphStrokeOpacity = 0.92;
    private static readonly TimeSpan MiniGraphFadeDuration = TimeSpan.FromSeconds(5);
    private Storyboard? _miniGraphFadeStoryboard;

    private static readonly SolidColorBrush SpeedStatsMaxCompleteBrush = FreezeBrush("#44b759");
    private static readonly SolidColorBrush SpeedStatsAvgCompleteBrush = FreezeBrush("#b7b044");

    private static SolidColorBrush FreezeBrush(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private readonly SpeedTestService _speedTest = new(new System.Net.Http.HttpClient());
    private CancellationTokenSource? _speedTestCts;

    private double _currentMbps;
    public double CurrentMbps
    {
        get => _currentMbps;
        set { _currentMbps = value; OnPropertyChanged(nameof(CurrentMbps)); }
    }

    private string _startStopText = "Старт";
    public string StartStopText
    {
        get => _startStopText;
        set { _startStopText = value; OnPropertyChanged(nameof(StartStopText)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly List<double> _graphSamples = new();
    private double _graphSmoothedMbps;
    private bool _graphSmoothPrimed;
    /// <summary>Последнее значение на циферблате; -1 — после сброса, первый сэмпл без анимации.</summary>
    private int _lastDialSpeed = -1;

    private double _sessionMaxMbps;
    private bool _sessionAverageKnown;
    private double _sessionAverageMbps;
    /// <summary>Идёт активный замер: средняя — плейсхолдер; иначе (покой) — «0 Мбит/с», если нет сохранённого результата.</summary>
    private bool _sessionMeasuring;

    internal static int TooltipInitialShowDelayMs { get; private set; }

    private readonly int _tooltipDelayMs;

    private AppSettings _settings = null!;
    private bool _trayHideAnimating;
    private bool _trayShowAnimating;
    private bool _centeredOnWorkAreaOnce;

    private const int TrayFlyDurationMs = 400;
    private const double TrayFlyEndScale = 0.14;

    internal AppSettings SettingsModel => _settings;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _tooltipDelayMs = CoerceTooltipDelayMs(TryFindResource("Rf.Tooltip.InitialShowDelayMs"));
        TooltipInitialShowDelayMs = _tooltipDelayMs;
        ReloadFromStorage();
        MiniGraphFillPath.Fill = BrushFromHex(MiniGraphFillColorHex);
        ResetSessionSpeedStats();
        Loaded += MainWindow_OnLoaded;
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLocalizationLanguageChanged;
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private async void StartStop_OnClick(object sender, RoutedEventArgs e)
    {
        if (_speedTestCts != null)
        {
            try { _speedTestCts.Cancel(); } catch { /* ignore */ }
            CurrentMbps = 0;
            _lastDialSpeed = -1;
            SpeedDial.SetValueSilent(0);
            StopMiniGraphFadeAndResetOpacity();
            _graphSamples.Clear();
            _graphSmoothPrimed = false;
            ClearMiniGraphPaths();
            ResetSessionSpeedStats();
            return;
        }

        await TryStartSpeedTestAsync(showMissingUrlMessage: true);
    }

    private void TryInitialWindowOpenAutoMeasure()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible)
                return;
            var s = SettingsStorage.Load();
            if (s.MinimizeToTrayOnStartup)
                return;
            TryAutoStartMeasureWhenWindowShownCore();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void ScheduleAutoStartMeasureIfNeeded()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible)
                return;
            TryAutoStartMeasureWhenWindowShownCore();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void TryAutoStartMeasureWhenWindowShownCore()
    {
        var s = SettingsStorage.Load();
        if (!s.SpeedTest.AutoStartWhenWindowOpen)
            return;
        if (_speedTestCts != null)
            return;
        _ = TryStartSpeedTestAsync(showMissingUrlMessage: false);
    }

    private async Task TryStartSpeedTestAsync(bool showMissingUrlMessage)
    {
        var s = SettingsStorage.Load();
        var fileUrlRaw = SpeedTestConfig.DownloadFileUrl.Trim();
        if (string.IsNullOrWhiteSpace(fileUrlRaw))
        {
            if (showMissingUrlMessage)
                MessageBox.Show(LocalizationService.T("Tray.NoServerUrl"), LocalizationService.T("Common.AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StopMiniGraphFadeAndResetOpacity();
        _graphSamples.Clear();
        _graphSmoothPrimed = false;
        ClearMiniGraphPaths();
        _lastDialSpeed = -1;
        SpeedDial.SetValueSilent(0);
        ResetSessionSpeedStats();
        _sessionMeasuring = true;
        UpdateSpeedStatsLine();

        StartStopText = "Стоп";
        _speedTestCts = new CancellationTokenSource();
        var ct = _speedTestCts.Token;

        try
        {
            var duration = TimeSpan.FromSeconds(Math.Clamp(s.SpeedTestDurationSeconds, 1, 600));
            var dlBase = new Uri(fileUrlRaw, UriKind.Absolute);
            var options = new SpeedTestOptions
            {
                Duration = duration,
                ParallelStreams = 4,
                ProgressInterval = TimeSpan.FromMilliseconds(50),
                DownloadUrlBuilder = () => new Uri(dlBase + (dlBase.Query.Length > 0 ? "&" : "?") + "r=" + Guid.NewGuid().ToString("N"))
            };

            var prog = new Progress<SpeedTestProgress>(p => Dispatcher.Invoke(() => ApplySpeedProgress(p)));
            var result = await _speedTest.RunAsync(options, prog, ct);

            var avg = result.Download.AverageMbps;
            CurrentMbps = avg;
            var rFinal = (int)Math.Clamp(Math.Round(avg), 0, 999);
            if (_lastDialSpeed < 0)
                SpeedDial.SetValueSilent(rFinal);
            else if (rFinal != _lastDialSpeed)
                SpeedDial.AnimateToValue(rFinal, slideDown: rFinal < _lastDialSpeed);
            _lastDialSpeed = rFinal;

            _sessionAverageKnown = true;
            _sessionAverageMbps = avg;
            UpdateSpeedStatsLine();
            SpeedDial.SetResultHighlight(true);
            BeginMiniGraphFadeOutAfterMeasurementComplete();
        }
        catch (OperationCanceledException)
        {
            StopMiniGraphFadeAndResetOpacity();
            _graphSamples.Clear();
            _graphSmoothPrimed = false;
            ClearMiniGraphPaths();
            ResetSessionSpeedStats();
        }
        catch
        {
            StopMiniGraphFadeAndResetOpacity();
            _graphSamples.Clear();
            _graphSmoothPrimed = false;
            ClearMiniGraphPaths();
            ResetSessionSpeedStats();
        }
        finally
        {
            _sessionMeasuring = false;
            _speedTestCts?.Dispose();
            _speedTestCts = null;
            StartStopText = "Старт";
        }
    }

    private void ApplySpeedProgress(SpeedTestProgress p)
    {
        if (p.Kind == SpeedTestProgressKind.Rate && p.Mbps is double mbps)
        {
            var v = Math.Max(0, mbps);
            CurrentMbps = v;

            var rounded = (int)Math.Clamp(Math.Round(v), 0, 999);
            if (_lastDialSpeed < 0)
                SpeedDial.SetValueSilent(rounded);
            else if (rounded != _lastDialSpeed)
                SpeedDial.AnimateToValue(rounded, slideDown: rounded < _lastDialSpeed);
            _lastDialSpeed = rounded;

            _sessionMaxMbps = Math.Max(_sessionMaxMbps, v);
            UpdateSpeedStatsLine();

            // Мягкая линия на графике: EMA по скорости.
            const double smoothAlpha = 0.22;
            if (!_graphSmoothPrimed)
            {
                _graphSmoothedMbps = v;
                _graphSmoothPrimed = true;
            }
            else
                _graphSmoothedMbps = smoothAlpha * v + (1.0 - smoothAlpha) * _graphSmoothedMbps;

            AddGraphSample(_graphSmoothedMbps);
        }
    }

    private void AddGraphSample(double mbps)
    {
        _graphSamples.Add(mbps);
        const int max = 50;
        while (_graphSamples.Count > max)
            _graphSamples.RemoveAt(0);

        RedrawMiniGraph();
    }

    private void GraphClipHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGraphCornerClip();
        RedrawMiniGraph();
    }

    private void UpdateGraphCornerClip()
    {
        const double radius = 10;
        var w = GraphClipHost.ActualWidth;
        var h = GraphClipHost.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            GraphClipHost.Clip = null;
            return;
        }

        GraphClipHost.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

    private void RedrawMiniGraph()
    {
        var w = GraphCanvas.ActualWidth;
        var h = GraphCanvas.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            ClearMiniGraphPaths();
            return;
        }

        const int maxSamples = 50;
        var n = _graphSamples.Count;
        if (n < 2)
        {
            ClearMiniGraphPaths();
            return;
        }

        var maxVal = Math.Max(10, _graphSamples.Max());
        var stepX = w / (maxSamples - 1);
        const double padY = 3;
        var baselineY = h - padY;
        var pts = new List<System.Windows.Point>(n);
        for (var i = 0; i < n; i++)
        {
            var x = i * stepX;
            var y = h - (_graphSamples[i] / maxVal) * (h - padY * 2) - padY;
            pts.Add(new System.Windows.Point(x, y));
        }

        MiniGraphFillPath.Data = BuildFilledAreaGeometry(pts, baselineY);
        MiniGraphPath.Data = BuildSmoothStrokeGeometry(pts);
    }

    private void ClearMiniGraphPaths()
    {
        MiniGraphFillPath.Data = null;
        MiniGraphPath.Data = null;
    }

    private void StopMiniGraphFadeAndResetOpacity()
    {
        if (_miniGraphFadeStoryboard != null)
        {
            try { _miniGraphFadeStoryboard.Stop(GraphClipHost); } catch { /* ignore */ }
            _miniGraphFadeStoryboard = null;
        }

        // Снять Storyboard с Opacity иначе HoldEnd оставляет 0 — новый график не виден.
        MiniGraphPath.BeginAnimation(UIElement.OpacityProperty, null);
        MiniGraphFillPath.BeginAnimation(UIElement.OpacityProperty, null);
        MiniGraphPath.Opacity = MiniGraphStrokeOpacity;
        MiniGraphFillPath.Opacity = 1.0;
    }

    private void BeginMiniGraphFadeOutAfterMeasurementComplete()
    {
        if (_graphSamples.Count < 2)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            StopMiniGraphFadeAndResetOpacity();

            var sb = new Storyboard();
            var dur = new Duration(MiniGraphFadeDuration);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };

            var strokeAnim = new DoubleAnimation
            {
                From = MiniGraphStrokeOpacity,
                To = 0,
                Duration = dur,
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(strokeAnim, MiniGraphPath);
            Storyboard.SetTargetProperty(strokeAnim, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(strokeAnim);

            var fillAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 0,
                Duration = dur,
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(fillAnim, MiniGraphFillPath);
            Storyboard.SetTargetProperty(fillAnim, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(fillAnim);

            void OnFadeCompleted(object? _, EventArgs __)
            {
                sb.Completed -= OnFadeCompleted;
                _miniGraphFadeStoryboard = null;
                ClearMiniGraphPaths();
                _graphSamples.Clear();
                _graphSmoothPrimed = false;
                MiniGraphPath.BeginAnimation(UIElement.OpacityProperty, null);
                MiniGraphFillPath.BeginAnimation(UIElement.OpacityProperty, null);
                MiniGraphPath.Opacity = MiniGraphStrokeOpacity;
                MiniGraphFillPath.Opacity = 1.0;
            }

            sb.Completed += OnFadeCompleted;
            _miniGraphFadeStoryboard = sb;
            sb.Begin(GraphClipHost, true);
        }), DispatcherPriority.Normal);
    }

    /// <summary>Замкнутая область под сглаженной кривой до линии нуля.</summary>
    private static PathGeometry BuildFilledAreaGeometry(IReadOnlyList<System.Windows.Point> pts, double baselineY)
    {
        var g = new PathGeometry();
        if (pts.Count < 2)
            return g;

        var fig = new PathFigure
        {
            StartPoint = new System.Windows.Point(pts[0].X, baselineY),
            IsClosed = true,
            IsFilled = true
        };
        fig.Segments.Add(new LineSegment(pts[0], true));
        AppendSmoothBezierSegments(fig, pts);
        fig.Segments.Add(new LineSegment(new System.Windows.Point(pts[pts.Count - 1].X, baselineY), true));

        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    /// <summary>Catmull-Rom → кубические Безье для мягкой линии без рывков по углам.</summary>
    private static PathGeometry BuildSmoothStrokeGeometry(IReadOnlyList<System.Windows.Point> pts)
    {
        var g = new PathGeometry();
        if (pts.Count < 2)
            return g;

        var fig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
        AppendSmoothBezierSegments(fig, pts);

        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    private static void AppendSmoothBezierSegments(PathFigure fig, IReadOnlyList<System.Windows.Point> pts)
    {
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = i > 0 ? pts[i - 1] : pts[i];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i < pts.Count - 2 ? pts[i + 2] : pts[i + 1];
            var c1 = new System.Windows.Point(
                p1.X + (p2.X - p0.X) / 6.0,
                p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new System.Windows.Point(
                p2.X - (p3.X - p1.X) / 6.0,
                p2.Y - (p3.Y - p1.Y) / 6.0);
            fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.LanguageChanged += OnLocalizationLanguageChanged;
        if (!_centeredOnWorkAreaOnce && TryGetWindowSizeForPlacement(out var w, out var h))
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - w) / 2;
            Top = wa.Top + (wa.Height - h) / 2;
            _centeredOnWorkAreaOnce = true;
        }

        ClampWindowToWorkArea();
        RedrawMiniGraph();
        UpdateSpeedStatsLine();
        TryInitialWindowOpenAutoMeasure();
    }

    private void OnLocalizationLanguageChanged()
    {
        ApplyCloseButtonAppearance();
        UpdateSpeedStatsLine();
    }

    private void ResetSessionSpeedStats()
    {
        _sessionMeasuring = false;
        _sessionMaxMbps = 0;
        _sessionAverageKnown = false;
        _sessionAverageMbps = 0;
        SpeedDial.SetResultHighlight(false);
        UpdateSpeedStatsLine();
    }

    private void UpdateSpeedStatsLine()
    {
        var unit = LocalizationService.T("MainWindow.SpeedStatsUnit");
        var maxStr = Math.Round(Math.Max(0, _sessionMaxMbps)).ToString("0", CultureInfo.InvariantCulture);
        string avgContent;
        if (_sessionAverageKnown)
            avgContent = $"{Math.Round(Math.Max(0, _sessionAverageMbps)).ToString("0", CultureInfo.InvariantCulture)} {unit}";
        else if (_sessionMeasuring)
            avgContent = $"{LocalizationService.T("MainWindow.SpeedStatsAveragePending")} {unit}";
        else
            avgContent = $"0 {unit}";

        var lineBrush = TryFindResource("Rf.Text.Primary") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.White;

        if (_sessionAverageKnown)
        {
            var avgNumStr = Math.Round(Math.Max(0, _sessionAverageMbps)).ToString("0", CultureInfo.InvariantCulture);
            SpeedStatsLine.Inlines.Clear();
            SpeedStatsLine.Foreground = System.Windows.Media.Brushes.Transparent;
            SpeedStatsLine.Inlines.Add(new Run(LocalizationService.T("MainWindow.SpeedStatsMaxPrefix")) { Foreground = lineBrush });
            SpeedStatsLine.Inlines.Add(new Run(maxStr) { Foreground = SpeedStatsMaxCompleteBrush });
            SpeedStatsLine.Inlines.Add(new Run($" {unit}. ") { Foreground = lineBrush });
            SpeedStatsLine.Inlines.Add(new Run(LocalizationService.T("MainWindow.SpeedStatsAvgPrefix")) { Foreground = lineBrush });
            SpeedStatsLine.Inlines.Add(new Run(avgNumStr) { Foreground = SpeedStatsAvgCompleteBrush });
            SpeedStatsLine.Inlines.Add(new Run($" {unit}.") { Foreground = lineBrush });
        }
        else
        {
            SpeedStatsLine.Inlines.Clear();
            SpeedStatsLine.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Rf.Text.Primary");
            SpeedStatsLine.Text = LocalizationService.T("MainWindow.SpeedStatsLine", maxStr, unit, avgContent);
        }
    }

    private static int CoerceTooltipDelayMs(object? r) => r switch
    {
        int i => i,
        null => 100,
        IConvertible c => Convert.ToInt32(c, CultureInfo.InvariantCulture),
        _ => 100
    };

    public void ReloadFromStorage()
    {
        _settings = SettingsStorage.Load();
        LocalizationService.Apply(AppLanguageCatalog.Normalize(_settings.UiLanguage));
        ThemeService.Apply(UiTheme.Dark);

        ApplyCloseButtonAppearance();
        TooltipDelayHelper.Apply(this, _tooltipDelayMs);
        if (System.Windows.Application.Current is App app)
            app.RefreshTrayMenu();

        ClampWindowToWorkArea();

        UpdateSpeedStatsLine();
    }

    private static bool TryGetWindowSizeForPlacement(Window window, out double w, out double h)
    {
        w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0)
            return false;
        return true;
    }

    private bool TryGetWindowSizeForPlacement(out double w, out double h) =>
        TryGetWindowSizeForPlacement(this, out w, out h);

    private void ClampWindowToWorkArea()
    {
        if (!TryGetWindowSizeForPlacement(out var w, out var h))
            return;
        var wa = SystemParameters.WorkArea;
        var maxLeft = wa.Left + wa.Width - w;
        var maxTop = wa.Top + wa.Height - h;
        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;
        if (Left > maxLeft) Left = maxLeft;
        if (Top > maxTop) Top = maxTop;
    }

    private void ApplyCloseButtonAppearance()
    {
        var trayMode = _settings.MinimizeToTrayOnCloseClick;
        BtnClose.ToolTip = trayMode
            ? LocalizationService.T("MainWindow.TooltipCloseToTray")
            : LocalizationService.T("MainWindow.TooltipCloseExit");
        var key = trayMode ? "Rf.TitleBar.Button" : "Rf.TitleBar.Close";
        if (TryFindResource(key) is Style st)
            BtnClose.Style = st;
    }

    private void OpenSettings_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow.ShowSingletonOrActivate(dlg =>
        {
            dlg.Owner = this;
            dlg.CenterOnWorkAreaAfterLoad = false;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dlg.LoadSettings(_settings);
        });
    }

    private void TitleBar_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void TitleBarAbout_OnClick(object sender, RoutedEventArgs e) => About.Show(this);

    private void TitleBarAbout_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        About.Show(this);
    }

    private void TitleBarMinimize_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void TitleBarMinimizeToTray_OnClick(object sender, RoutedEventArgs e)
    {
        HideToTrayCore(_settings);
    }

    private void TitleBarClose_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settings.MinimizeToTrayOnCloseClick)
        {
            HideToTrayCore(_settings);
            return;
        }

        App.TryShowAutostartPromptOnFirstExit();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.ShutdownRequested)
        {
            base.OnClosing(e);
            return;
        }

        var s = SettingsStorage.Load();
        if (s.MinimizeToTrayOnCloseClick)
        {
            e.Cancel = true;
            HideToTrayCore(s);
            return;
        }

        App.TryShowAutostartPromptOnFirstExit();
        base.OnClosing(e);
    }

    private void HideToTrayCore(AppSettings s)
    {
        if (!s.MinimizeToTrayAnimationEnabled)
        {
            Hide();
            return;
        }

        if (_trayHideAnimating || _trayShowAnimating)
            return;

        _trayHideAnimating = true;
        var origLeft = Left;
        var origTop = Top;
        GetFlyToTrayTarget(origLeft, origTop, out var endLeft, out var endTop);

        var duration = TimeSpan.FromMilliseconds(TrayFlyDurationMs);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };

        var sb = new Storyboard();

        var animLeft = new DoubleAnimation(origLeft, endLeft, duration) { EasingFunction = easing };
        Storyboard.SetTarget(animLeft, this);
        Storyboard.SetTargetProperty(animLeft, new PropertyPath(LeftProperty));
        sb.Children.Add(animLeft);

        var animTop = new DoubleAnimation(origTop, endTop, duration) { EasingFunction = easing };
        Storyboard.SetTarget(animTop, this);
        Storyboard.SetTargetProperty(animTop, new PropertyPath(TopProperty));
        sb.Children.Add(animTop);

        var fade = new DoubleAnimation(Opacity, 0, duration) { EasingFunction = easing };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);

        var sx = new DoubleAnimation(TrayHideScale.ScaleX, TrayFlyEndScale, duration) { EasingFunction = easing };
        Storyboard.SetTarget(sx, TrayHideScale);
        Storyboard.SetTargetProperty(sx, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(sx);

        var sy = new DoubleAnimation(TrayHideScale.ScaleY, TrayFlyEndScale, duration) { EasingFunction = easing };
        Storyboard.SetTarget(sy, TrayHideScale);
        Storyboard.SetTargetProperty(sy, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(sy);

        sb.Completed += (_, _) =>
        {
            _trayHideAnimating = false;
            sb.Remove(this);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = origLeft;
            Top = origTop;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            TrayHideScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TrayHideScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TrayHideScale.ScaleX = 1;
            TrayHideScale.ScaleY = 1;
            Hide();
        };

        sb.Begin(this, true);
    }

    internal void ShowFromTray()
    {
        ReloadFromStorage();

        if (WindowState == WindowState.Minimized)
        {
            ShowCore();
            return;
        }

        if (IsVisible)
        {
            WindowState = WindowState.Normal;
            Topmost = true;
            Topmost = false;
            Activate();
            ScheduleAutoStartMeasureIfNeeded();
            return;
        }

        if (!_settings.MinimizeToTrayAnimationEnabled)
        {
            ShowCore();
            return;
        }

        if (_trayShowAnimating || _trayHideAnimating)
            return;

        var endLeft = Left;
        var endTop = Top;
        GetFlyToTrayTarget(endLeft, endTop, out var startLeft, out var startTop);

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(OpacityProperty, null);
        TrayHideScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TrayHideScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        Left = startLeft;
        Top = startTop;
        Opacity = 0;
        TrayHideScale.ScaleX = 1;
        TrayHideScale.ScaleY = 1;

        Show();
        WindowState = WindowState.Normal;

        _trayShowAnimating = true;
        var duration = TimeSpan.FromMilliseconds(TrayFlyDurationMs);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var sb = new Storyboard();

        var animLeft = new DoubleAnimation(startLeft, endLeft, duration) { EasingFunction = easing };
        Storyboard.SetTarget(animLeft, this);
        Storyboard.SetTargetProperty(animLeft, new PropertyPath(LeftProperty));
        sb.Children.Add(animLeft);

        var animTop = new DoubleAnimation(startTop, endTop, duration) { EasingFunction = easing };
        Storyboard.SetTarget(animTop, this);
        Storyboard.SetTargetProperty(animTop, new PropertyPath(TopProperty));
        sb.Children.Add(animTop);

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);

        sb.Completed += (_, _) =>
        {
            _trayShowAnimating = false;
            sb.Remove(this);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = endLeft;
            Top = endTop;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            TrayHideScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TrayHideScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TrayHideScale.ScaleX = 1;
            TrayHideScale.ScaleY = 1;
            Topmost = true;
            Topmost = false;
            Activate();
            ScheduleAutoStartMeasureIfNeeded();
        };

        sb.Begin(this, true);
    }

    private void ShowCore()
    {
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Topmost = false;
        Activate();
        ScheduleAutoStartMeasureIfNeeded();
    }

    private void GetFlyToTrayTarget(double origLeft, double origTop, out double endLeft, out double endTop)
    {
        const double horizontalFactor = 0.38;
        const double verticalFactor = 1.0;

        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;

        double cornerLeft;
        double cornerTop;

        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            try
            {
                var handle = new WindowInteropHelper(this).EnsureHandle();
                var wa = WinFormsScreen.FromHandle(handle).WorkingArea;
                var tf = src.CompositionTarget.TransformFromDevice;
                var br = tf.Transform(new System.Windows.Point(wa.Right, wa.Bottom));
                cornerLeft = br.X - w;
                cornerTop = br.Y - h;
                endLeft = origLeft + (cornerLeft - origLeft) * horizontalFactor;
                endTop = origTop + (cornerTop - origTop) * verticalFactor;
                return;
            }
            catch
            {
                // fallback ниже
            }
        }

        var waDip = SystemParameters.WorkArea;
        cornerLeft = waDip.Right - w;
        cornerTop = waDip.Bottom - h;
        endLeft = origLeft + (cornerLeft - origLeft) * horizontalFactor;
        endTop = origTop + (cornerTop - origTop) * verticalFactor;
    }
}
