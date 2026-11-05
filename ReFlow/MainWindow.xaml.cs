using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
using System.Windows.Shapes;
using System.Windows.Shell;
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
    private static readonly SolidColorBrush DialOutlineMeasureBrush = FreezeBrush("#68e2cf");
    private static readonly SolidColorBrush DialOutlineOfflineBrush = FreezeBrush("#E23B3B");

    private static SolidColorBrush FreezeBrush(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void InitDialProgressOutline()
    {
        var geo = CreateSpeedDialOutlineGeometry();
        DialProgressOutline.Data = geo;
        DialCompleteGlow.Data = geo;
    }

    /// <summary>Периметр скруглённого прямоугольника (fallback, если не удалось снять длину с геометрии).</summary>
    private static double RoundedRectPerimeter(double w, double h, double r) =>
        2 * (w - 2 * r) + 2 * (h - 2 * r) + 2 * Math.PI * r;

    /// <summary>Периметр контура в тех же единицах, что геометрия циферблата — полный круг ожидания ровно за интервал пробника.</summary>
    private static double DialOutlineAnalyticalPerimeter()
    {
        var inset = DialProgressGeometryInset;
        var wIn = DialOutlineWidth - 2 * inset;
        var hIn = DialOutlineHeight - 2 * inset;
        var rIn = Math.Max(0, DialCornerRadius - inset);
        return RoundedRectPerimeter(wIn, hIn, rIn);
    }

    /// <summary>
    /// Внутрь от внешней границы циферблата: центр линии прогресса не совпадает с <c>Border</c> рамки (1.5),
    /// иначе на углах два штриха с антиалиасингом накладываются и кажутся вдвое толще.
    /// ≈ половина BorderThickness + половина StrokeThickness + зазор.
    /// </summary>
    private const double DialProgressGeometryInset = 1;

    /// <summary>Контур прогресса внутри рамки циферблата; старт в центре верхней стороны, обход по часовой. Без <c>IsClosed</c>.</summary>
    private static PathGeometry CreateSpeedDialOutlineGeometry()
    {
        var inset = DialProgressGeometryInset;
        var w = DialOutlineWidth - 2 * inset;
        var h = DialOutlineHeight - 2 * inset;
        var r = Math.Max(0, DialCornerRadius - inset);
        var ox = inset;
        var oy = inset;
        var midX = ox + w * 0.5;
        var fig = new PathFigure
        {
            StartPoint = new System.Windows.Point(midX, oy),
            IsClosed = false
        };
        fig.Segments.Add(new LineSegment(new System.Windows.Point(ox + w - r, oy), true));
        fig.Segments.Add(new ArcSegment(new System.Windows.Point(ox + w, oy + r), new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new System.Windows.Point(ox + w, oy + h - r), true));
        fig.Segments.Add(new ArcSegment(new System.Windows.Point(ox + w - r, oy + h), new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new System.Windows.Point(ox + r, oy + h), true));
        fig.Segments.Add(new ArcSegment(new System.Windows.Point(ox, oy + h - r), new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new System.Windows.Point(ox, oy + r), true));
        fig.Segments.Add(new ArcSegment(new System.Windows.Point(ox + r, oy), new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new System.Windows.Point(midX, oy), true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    /// <summary>Длина обводки в тех же единицах, что <see cref="Shape.StrokeDashOffset"/> (flatten WPF, не аналитика).</summary>
    private static double MeasureFlattenedPathLength(PathGeometry geometry)
    {
        try
        {
            var flat = geometry.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute);
            double len = 0;
            foreach (PathFigure fig in flat.Figures)
            {
                var cur = fig.StartPoint;
                var start = cur;
                foreach (PathSegment seg in fig.Segments)
                {
                    switch (seg)
                    {
                        case LineSegment ls:
                        {
                            var next = ls.Point;
                            len += SegmentLen(cur, next);
                            cur = next;
                            break;
                        }
                        case PolyLineSegment poly:
                        {
                            foreach (System.Windows.Point pt in poly.Points)
                            {
                                len += SegmentLen(cur, pt);
                                cur = pt;
                            }
                            break;
                        }
                        default:
                            return 0;
                    }
                }
                if (fig.IsClosed)
                    len += SegmentLen(cur, start);
            }
            return len > double.Epsilon ? len : 0;
        }
        catch
        {
            return 0;
        }

        static double SegmentLen(System.Windows.Point a, System.Windows.Point b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>Flatten иногда даёт длину в разы больше аналитики — тогда штрих «догоняет» время визуально раньше.</summary>
    private static double CoerceOutlineStrokeLength(PathGeometry geo, double analytical)
    {
        var m = MeasureFlattenedPathLength(geo);
        if (m <= double.Epsilon)
            return analytical;
        var ratio = m / analytical;
        return ratio is >= 0.82 and <= 1.18 ? m : analytical;
    }

    private double _dialRingPathLength = 1;
    private TimeSpan _dialRingDuration;
    private Stopwatch? _dialRingUiSw;
    /// <summary>Красная обводка ожидания сети: заполнение по кругу зациклено, не привязано к длительности замера.</summary>
    private bool _dialRingWaiting;

    private void PlayMeasureCompleteGlow()
    {
        if (DialCompleteGlow == null)
            return;

        DialCompleteGlow.BeginAnimation(UIElement.OpacityProperty, null);

        DialCompleteGlow.Visibility = Visibility.Visible;
        DialCompleteGlow.Opacity = 1;

        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(2000)))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            DialCompleteGlow.BeginAnimation(UIElement.OpacityProperty, null);
            DialCompleteGlow.Opacity = 0;
            DialCompleteGlow.Visibility = Visibility.Collapsed;
        };

        DialCompleteGlow.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void ClearTaskbarProgress()
    {
        if (TaskbarInfo == null)
            return;
        TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
        TaskbarInfo.ProgressValue = 0;
    }

    private void SetTaskbarProgress(double t)
    {
        if (TaskbarInfo == null)
            return;
        TaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
        TaskbarInfo.ProgressValue = Math.Clamp(t, 0, 1);
    }

    private void EnsureDialOutlinePathMetrics()
    {
        var geo = DialProgressOutline.Data as PathGeometry ?? CreateSpeedDialOutlineGeometry();
        if (DialProgressOutline.Data is not PathGeometry)
            DialProgressOutline.Data = geo;

        var analytical = DialOutlineAnalyticalPerimeter();
        _dialRingPathLength = CoerceOutlineStrokeLength(geo, analytical);
    }

    private void BeginDialProgressRing(TimeSpan totalDuration)
    {
        StopDialRingUiTimer();

        _dialRingDuration = totalDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : totalDuration;
        EnsureDialOutlinePathMetrics();

        // Прогресс — длина первого штриха [0..len], offset=0: доля времени линейна по длине нарисованного контура.
        // Сдвиг offset при штрихе ≈ периметра на замкнутом Path даёт преждевременное визуальное замыкание.
        DialProgressOutline.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        DialProgressOutline.BeginAnimation(Shape.StrokeDashArrayProperty, null);
        DialProgressOutline.Stroke = DialOutlineMeasureBrush;
        DialProgressOutline.StrokeDashOffset = 0;
        DialProgressOutline.StrokeDashArray = new DoubleCollection { 0, 1_000_000 };
        DialProgressOutline.Visibility = Visibility.Visible;

        // Каждый кадр отрисовки — плавнее, чем DispatcherTimer ~50 ms.
        _dialRingUiSw = Stopwatch.StartNew();
        CompositionTarget.Rendering += OnDialRingRendering;

        SetTaskbarProgress(0);
    }

    private void OnDialRingRendering(object? sender, EventArgs e)
    {
        if (_dialRingUiSw is null || DialProgressOutline.Visibility != Visibility.Visible)
            return;
        if (_dialRingWaiting)
        {
            UpdateWaitingDialOutlineFromElapsed(_dialRingUiSw.Elapsed);
            return;
        }

        UpdateDialProgressRingFromElapsed(_dialRingUiSw.Elapsed, _dialRingDuration);
    }

    private void StopDialRingUiTimer()
    {
        CompositionTarget.Rendering -= OnDialRingRendering;
        _dialRingUiSw = null;
        _dialRingWaiting = false;
    }

    /// <summary>Доля контура по прошедшему времени; во время замера вызывается из <see cref="OnDialRingRendering"/>.</summary>
    private void UpdateDialProgressRingFromElapsed(TimeSpan elapsed, TimeSpan? plannedTotal = null, bool forceFull = false)
    {
        if (DialProgressOutline.Visibility != Visibility.Visible)
            return;
        var denom = plannedTotal ?? _dialRingDuration;
        if (denom <= TimeSpan.Zero || _dialRingPathLength <= 0)
            return;
        // Taskbar-прогресс должен строго соответствовать длительности замера из настроек (без визуального замедления).
        var taskbarT = forceFull ? 1.0 : Math.Clamp(elapsed.Ticks / (double)denom.Ticks, 0, 1);
        SetTaskbarProgress(taskbarT);

        var t = forceFull
            ? 1.0
            : Math.Clamp(elapsed.Ticks / ((double)denom.Ticks * DialRingProgressSlowdown), 0, 1);
        var lit = Math.Clamp(_dialRingPathLength * t, 0, _dialRingPathLength);
        // Без BeginAnimation на каждом кадре — иначе рывки и лишняя нагрузка.
        DialProgressOutline.StrokeDashOffset = 0;
        DialProgressOutline.StrokeDashArray = new DoubleCollection { lit, 1_000_000 };
    }

    /// <summary>Сброс кольца при «Стоп» / отмене: как до замера (нет частичного прогресса).</summary>
    private void ResetDialProgressRingAfterAbort()
    {
        StopDialRingUiTimer();
        DialProgressOutline.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        DialProgressOutline.BeginAnimation(Shape.StrokeDashArrayProperty, null);
        DialProgressOutline.StrokeDashOffset = 0;
        DialProgressOutline.StrokeDashArray = new DoubleCollection { 0, 1_000_000 };
        DialProgressOutline.Stroke = DialOutlineMeasureBrush;
        DialProgressOutline.Visibility = Visibility.Collapsed;
        ClearTaskbarProgress();
    }

    /// <summary>Красная обводка ожидания: тот же ход, что у замера, но круг за кругом, пока нет сети.</summary>
    private void ShowOfflineDialOutline()
    {
        StopDialRingUiTimer();
        EnsureDialOutlinePathMetrics();
        _dialRingPathLength = DialOutlineAnalyticalPerimeter();

        DialProgressOutline.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        DialProgressOutline.BeginAnimation(Shape.StrokeDashArrayProperty, null);
        DialProgressOutline.Stroke = DialOutlineOfflineBrush;
        DialProgressOutline.StrokeDashOffset = 0;
        DialProgressOutline.StrokeDashArray = new DoubleCollection { 0, 1_000_000 };
        DialProgressOutline.Visibility = Visibility.Visible;

        _dialRingWaiting = true;
        _dialRingUiSw = Stopwatch.StartNew();
        CompositionTarget.Rendering += OnDialRingRendering;

        if (TaskbarInfo != null)
        {
            TaskbarInfo.ProgressState = TaskbarItemProgressState.Error;
            TaskbarInfo.ProgressValue = 1;
        }
    }

    private void UpdateWaitingDialOutlineFromElapsed(TimeSpan elapsed)
    {
        if (_dialRingPathLength <= 0 || ConnectivityProbe.RetryInterval <= TimeSpan.Zero)
            return;

        var periodTicks = ConnectivityProbe.RetryInterval.Ticks;
        double t;
        if (elapsed.Ticks <= 0)
        {
            t = 0;
        }
        else
        {
            var cycle = elapsed.Ticks % periodTicks;
            // Ровно на границе круга показываем полный контур, затем следующий круг с нуля.
            t = cycle == 0 ? 1.0 : cycle / (double)periodTicks;
        }

        var lit = Math.Clamp(_dialRingPathLength * t, 0, _dialRingPathLength);
        DialProgressOutline.StrokeDashOffset = 0;
        DialProgressOutline.StrokeDashArray = new DoubleCollection { lit, 1_000_000 };
    }

    private void EnterWaitingForNetworkUi()
    {
        _sessionWaitingForNetwork = true;
        ShowOfflineDialOutline();
        UpdateSpeedStatsLine();
    }

    /// <summary>Останавливает анимацию по времени; контур остаётся на экране до следующего замера.</summary>
    private void StopDialRingSmoothingAfterMeasure()
    {
        StopDialRingUiTimer();

        var holdOffset = (double)DialProgressOutline.GetValue(Shape.StrokeDashOffsetProperty);
        var holdDash = DialProgressOutline.StrokeDashArray != null
            ? new DoubleCollection(DialProgressOutline.StrokeDashArray)
            : new DoubleCollection { 0, 1_000_000 };
        DialProgressOutline.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        DialProgressOutline.BeginAnimation(Shape.StrokeDashArrayProperty, null);
        DialProgressOutline.StrokeDashOffset = holdOffset;
        DialProgressOutline.StrokeDashArray = holdDash;

        // В панели задач прогресс нужен только во время замера.
        ClearTaskbarProgress();
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
    /// <summary>Все значения Мбит/с из прогресса замера — для средней как среднее арифметическое по тикам.</summary>
    private readonly List<double> _sessionRateSamples = new();
    private double _graphSmoothedMbps;
    private bool _graphSmoothPrimed;
    /// <summary>Последнее значение на циферблате; -1 — после сброса, первый сэмпл без анимации.</summary>
    private int _lastDialSpeed = -1;

    private double _sessionMaxMbps;
    private bool _sessionAverageKnown;
    private double _sessionAverageMbps;
    /// <summary>Идёт активный замер: средняя — плейсхолдер; иначе (покой) — «0 Мбит/с», если нет сохранённого результата.</summary>
    private bool _sessionMeasuring;
    /// <summary>Старт нажат, сервер замера недоступен — красная обводка, повтор пробника.</summary>
    private bool _sessionWaitingForNetwork;

    internal static int TooltipInitialShowDelayMs { get; private set; }

    private readonly int _tooltipDelayMs;

    private AppSettings _settings = null!;
    private bool _trayHideAnimating;
    private bool _trayShowAnimating;
    private bool _centeredOnWorkAreaOnce;

    private const int TrayFlyDurationMs = 400;
    private const double TrayFlyEndScale = 0.14;

    /// <summary>Совпадает с циферблатом <c>SpeedRollingDigits</c> (278×130, r=20).</summary>
    private const double DialOutlineWidth = 278;
    private const double DialOutlineHeight = 130;
    private const double DialCornerRadius = 20;
    /// <summary>Визуальное заполнение контура в <c>N</c> раз медленнее, чем длительность замера по таймеру.</summary>
    private const double DialRingProgressSlowdown = 2.5;

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
        InitDialProgressOutline();
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
            ResetSessionSpeedStats();
            ResetDialProgressRingAfterAbort();
            CurrentMbps = 0;
            SpeedDial.AnimateToValue(0, slideDown: true);
            _lastDialSpeed = 0;
            StopMiniGraphFadeAndResetOpacity();
            _graphSamples.Clear();
            _sessionRateSamples.Clear();
            _graphSmoothPrimed = false;
            ClearMiniGraphPaths();
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
            // Один холодный старт: замер в трее уже стартует из App — не дублировать загрузкой окна.
            if (s.SpeedTest.AutoStartOnTrayStartup)
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
        var fileUrlRaw = ((s.SpeedTest.UseCustomDownloadFileUrl ? s.SpeedTest.DownloadFileUrl : null) ?? SpeedTestConfig.DownloadFileUrl).Trim();
        if (string.IsNullOrWhiteSpace(fileUrlRaw))
        {
            if (showMissingUrlMessage)
                MessageBox.Show(LocalizationService.T("Tray.NoServerUrl"), LocalizationService.T("Common.AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StopMiniGraphFadeAndResetOpacity();
        _graphSamples.Clear();
        _sessionRateSamples.Clear();
        _graphSmoothPrimed = false;
        ClearMiniGraphPaths();
        _lastDialSpeed = -1;
        SpeedDial.SetValueSilent(0);
        ResetSessionSpeedStats();
        UpdateSpeedStatsLine();

        StartStopText = "Стоп";
        _speedTestCts = new CancellationTokenSource();
        var ct = _speedTestCts.Token;

        try
        {
            var duration = TimeSpan.FromSeconds(Math.Clamp(s.SpeedTestDurationSeconds, 1, 600));
            var dlBase = new Uri(fileUrlRaw, UriKind.Absolute);
            await ConnectivityProbe.WaitUntilReachableAsync(
                dlBase,
                () => Dispatcher.Invoke(EnterWaitingForNetworkUi),
                ct);

            _sessionWaitingForNetwork = false;
            _sessionMeasuring = true;
            UpdateSpeedStatsLine();
            BeginDialProgressRing(duration);
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
            _sessionAverageMbps = _sessionRateSamples.Count > 0
                ? _sessionRateSamples.Average()
                : avg;
            UpdateSpeedStatsLine();
            SpeedDial.SetResultHighlight(true);
            BeginMiniGraphFadeOutAfterMeasurementComplete();
            UpdateDialProgressRingFromElapsed(duration, duration, forceFull: true);
            PlayMeasureCompleteGlow();
        }
        catch (OperationCanceledException)
        {
            ResetDialProgressRingAfterAbort();
            StopMiniGraphFadeAndResetOpacity();
            _graphSamples.Clear();
            _sessionRateSamples.Clear();
            _graphSmoothPrimed = false;
            ClearMiniGraphPaths();
            ResetSessionSpeedStats();
        }
        catch
        {
            ResetDialProgressRingAfterAbort();
            StopMiniGraphFadeAndResetOpacity();
            _graphSamples.Clear();
            _sessionRateSamples.Clear();
            _graphSmoothPrimed = false;
            ClearMiniGraphPaths();
            ResetSessionSpeedStats();
        }
        finally
        {
            StopDialRingSmoothingAfterMeasure();
            _sessionWaitingForNetwork = false;
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
            if (!_sessionMeasuring)
                return;

            var v = Math.Max(0, mbps);
            CurrentMbps = v;

            var rounded = (int)Math.Clamp(Math.Round(v), 0, 999);
            if (_lastDialSpeed < 0)
                SpeedDial.SetValueSilent(rounded);
            else if (rounded != _lastDialSpeed)
                SpeedDial.AnimateToValue(rounded, slideDown: rounded < _lastDialSpeed);
            _lastDialSpeed = rounded;

            _sessionMaxMbps = Math.Max(_sessionMaxMbps, v);
            _sessionRateSamples.Add(v);
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
        if (ConnectivityProbe.SimulateOffline)
            _ = TryStartSpeedTestAsync(showMissingUrlMessage: false);
        else
            TryInitialWindowOpenAutoMeasure();
    }

    private void OnLocalizationLanguageChanged()
    {
        ApplyCloseButtonAppearance();
        UpdateSpeedStatsLine();
        UpdateRegionLine();
    }

    // ---------- Регион над табло ----------

    private RegionService? _region;
    private DispatcherTimer? _regionBlinkTimer;
    private DateTime _regionCheckStartedUtc;
    private int _regionCheckSeq;
    /// <summary>Как у иконки в трее: быстрый ответ всё равно даёт заметное мигание.</summary>
    private static readonly TimeSpan RegionBlinkMinDuration = TimeSpan.FromMilliseconds(1200);

    /// <summary>Подключить строку региона к общей проверке (её запускает трей при старте).</summary>
    internal void AttachRegion(RegionService region)
    {
        _region = region;
        _regionBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _regionBlinkTimer.Tick += (_, _) => RegionFlagImage.Opacity = RegionFlagImage.Opacity < 1 ? 1 : 0.3;
        region.CheckStarted += OnRegionCheckStarted;
        region.CheckCompleted += OnRegionCheckCompleted;
        SettingsStorage.Saved += OnSettingsSavedForRegion;
        Closed += (_, _) =>
        {
            region.CheckStarted -= OnRegionCheckStarted;
            region.CheckCompleted -= OnRegionCheckCompleted;
            SettingsStorage.Saved -= OnSettingsSavedForRegion;
            _regionBlinkTimer.Stop();
        };
        ApplyRegionLineVisibility(SettingsStorage.Load());
        if (region.IsChecking)
            OnRegionCheckStarted();
        else
            UpdateRegionLine();
    }

    private void OnSettingsSavedForRegion(AppSettings settings) =>
        Dispatcher.InvokeAsync(() => ApplyRegionLineVisibility(settings));

    private void ApplyRegionLineVisibility(AppSettings settings) =>
        RegionLine.Visibility = settings.Tray.ShowRegionInWindow ? Visibility.Visible : Visibility.Hidden;

    private void OnRegionCheckStarted()
    {
        _regionCheckSeq++;
        _regionCheckStartedUtc = DateTime.UtcNow;
        RegionFlagImage.Opacity = 0.3;
        _regionBlinkTimer?.Start();
        UpdateRegionLine(checking: true);
    }

    private async void OnRegionCheckCompleted()
    {
        var seq = _regionCheckSeq;
        var remaining = RegionBlinkMinDuration - (DateTime.UtcNow - _regionCheckStartedUtc);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
        if (seq != _regionCheckSeq || _region is null or { IsChecking: true })
            return;
        _regionBlinkTimer?.Stop();
        RegionFlagImage.Opacity = 1;
        UpdateRegionLine();
    }

    private void UpdateRegionLine(bool? checking = null)
    {
        if (_region == null)
            return;
        var isChecking = checking ?? (_regionBlinkTimer?.IsEnabled == true);
        var code = _region.CountryCode;

        RegionFlagImage.Source = code != null ? TryLoadRegionFlag(code) : null;
        RegionUnknownMark.Visibility = code != null && RegionFlagImage.Source != null ? Visibility.Collapsed : Visibility.Visible;
        RegionVpnDot.Visibility = _region.VpnDetected ? Visibility.Visible : Visibility.Collapsed;

        string text;
        if (isChecking)
            text = LocalizationService.T("MainWindow.RegionChecking");
        else if (code == null)
            text = LocalizationService.T("MainWindow.RegionUnknown");
        else if (_region.VpnDetected && _region.RealCountryCode != null && _region.IpCountryCode != null)
            text = string.Format(CultureInfo.CurrentCulture, LocalizationService.T("MainWindow.RegionVpn"),
                RegionService.CountryName(_region.RealCountryCode), RegionService.CountryName(_region.IpCountryCode));
        else if (_region.RealCheckFailed)
            text = string.Format(CultureInfo.CurrentCulture, LocalizationService.T("MainWindow.RegionRealUnknown"),
                RegionService.CountryName(code));
        else
        {
            var name = RegionService.CountryName(code);
            text = _region.LastCheckFailed
                ? string.Format(CultureInfo.CurrentCulture, LocalizationService.T("MainWindow.RegionCheckFailed"), name)
                : _region.RealCountryCode != null
                    ? string.Format(CultureInfo.CurrentCulture, LocalizationService.T("MainWindow.RegionEnhanced"), name)
                    : name;
        }
        RegionCaption.Text = text;
    }

    private static ImageSource? TryLoadRegionFlag(string code)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage(
                new Uri($"pack://application:,,,/Flags/{code.ToLowerInvariant()}.png", UriKind.Absolute));
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ResetSessionSpeedStats()
    {
        _sessionMeasuring = false;
        _sessionWaitingForNetwork = false;
        _sessionMaxMbps = 0;
        _sessionRateSamples.Clear();
        _sessionAverageKnown = false;
        _sessionAverageMbps = 0;
        SpeedDial.SetResultHighlight(false);
        UpdateSpeedStatsLine();
    }

    private void UpdateSpeedStatsLine()
    {
        if (_sessionWaitingForNetwork)
        {
            SpeedStatsLine.Inlines.Clear();
            SpeedStatsLine.Foreground = DialOutlineOfflineBrush;
            SpeedStatsLine.Text = LocalizationService.T("MainWindow.WaitingForNetwork");
            return;
        }

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
        UpdateSpeedTestDomainLine();
    }

    private static string? TryGetMainDomainFromUrl(string? urlRaw)
    {
        if (string.IsNullOrWhiteSpace(urlRaw))
            return null;

        var raw = urlRaw.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && !Uri.TryCreate("https://" + raw, UriKind.Absolute, out uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
            return null;

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];

        return host;
    }

    private void UpdateSpeedTestDomainLine()
    {
        if (SpeedTestDomainLine == null)
            return;

        if (!_settings.SpeedTest.UseCustomDownloadFileUrl
            || string.IsNullOrWhiteSpace(_settings.SpeedTest.DownloadFileUrl))
        {
            SpeedTestDomainLine.Visibility = Visibility.Collapsed;
            SpeedTestDomainLine.Text = "";
            return;
        }

        var domain = TryGetMainDomainFromUrl(_settings.SpeedTest.DownloadFileUrl);
        if (string.IsNullOrWhiteSpace(domain))
        {
            SpeedTestDomainLine.Visibility = Visibility.Collapsed;
            SpeedTestDomainLine.Text = "";
            return;
        }

        SpeedTestDomainLine.Text = domain;
        SpeedTestDomainLine.Visibility = Visibility.Visible;
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
