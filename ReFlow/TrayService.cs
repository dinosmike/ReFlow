using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using ReFlow.Models;
using ReFlow.Services;
using Application = System.Windows.Application;

namespace ReFlow;

public sealed class TrayService : IDisposable
{
    /// <summary>Должно быть не меньше системного DoubleClickTime, иначе одиночный клик срабатывает раньше распознавания двойного.</summary>
    private static int SingleClickDelayMs =>
        Math.Clamp(SystemInformation.DoubleClickTime + 40, 200, 800);

    /// <summary>Ритм мигания: черточка ожидания, полоска под цифрами, флаг во время проверки.</summary>
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(200);

    private readonly NotifyIcon _notifyIcon;
    /// <summary>Картинка основной иконки: ровная или мигающая.</summary>
    private readonly TrayIconAnimator _iconAnim;
    /// <summary>WPF-диспетчер: WinForms <see cref="System.Windows.Forms.Timer"/> в чистом WPF часто не получает тики.</summary>
    private readonly DispatcherTimer _singleClickTimer;
    private readonly Dispatcher _dispatcher;
    private readonly UiTheme _theme;

    /// <summary>Вторая иконка — флаг страны внешнего IP; создаётся при первом включении в настройках.</summary>
    private NotifyIcon? _regionIcon;
    private TrayIconAnimator? _regionAnim;
    /// <summary>Проверка региона работает всегда: флаг показан и в главном окне, независимо от иконки в трее.</summary>
    private readonly RegionService _region;
    private bool _regionStarted;

    public RegionService Region => _region;

    /// <summary>Последний успешный пик (макс. скорость по сэмплам, зелёный), держим до следующего клика / перезапуска.</summary>
    private string? _lastQuickTestPeakText;

    /// <summary>После <see cref="OnMouseDoubleClick"/> приходит ещё MouseClick/MouseUp по второму клику — иначе они снова стартуют таймер и открывают окно.</summary>
    private DateTime _suppressSingleClickScheduleUtc;

    public TrayService(UiTheme theme)
    {
        _theme = theme;

        var disp = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Application.Current is null.");
        _dispatcher = disp;
        _singleClickTimer = new DispatcherTimer(DispatcherPriority.Normal, disp)
        {
            Interval = TimeSpan.FromMilliseconds(SingleClickDelayMs)
        };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            try
            {
                ExecuteTrayAction(isDouble: false);
            }
            catch
            {
                LogTrayException("ExecuteTrayAction(single)");
            }
        };

        _notifyIcon = new NotifyIcon
        {
            Text = LocalizationService.T("Tray.BalloonTip"),
        };
        _iconAnim = new TrayIconAnimator(_notifyIcon, disp, BlinkInterval);
        _iconAnim.Show(AppIconFactory.CreateTrayIcon(theme));
        _notifyIcon.Visible = true;
        // MouseUp + MouseClick: в разных версиях Windows для NotifyIcon приходит не всегда одно и то же.
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;

        _notifyIcon.ContextMenuStrip = CreateContextMenu();

        _region = new RegionService(disp);
        _region.CheckStarted += OnRegionCheckStarted;
        _region.CheckCompleted += OnRegionCheckCompleted;

        MeasurementSession.Started += OnMeasureStarted;
        MeasurementSession.WaitingForNetwork += OnMeasureWaiting;
        MeasurementSession.Measuring += OnMeasureMeasuring;
        MeasurementSession.Progress += OnMeasureProgress;
        MeasurementSession.Completed += OnMeasureCompleted;
        MeasurementSession.Aborted += OnMeasureAborted;

        SettingsStorage.Saved += OnSettingsSaved;
        ApplyRegionSettings(SettingsStorage.Load());
    }

    /// <summary>Перерисовать иконку трея под выбранную тему (цвета как в UI).</summary>
    public void RefreshIcon(UiTheme theme)
    {
        _iconAnim.Show(AppIconFactory.CreateTrayIcon(theme));
    }

    public void RefreshTexts()
    {
        _notifyIcon.Text = LocalizationService.T("Tray.BalloonTip");
        if (_notifyIcon.ContextMenuStrip != null)
            RebuildContextMenu(_notifyIcon.ContextMenuStrip);
        if (_regionIcon != null)
        {
            if (_regionIcon.ContextMenuStrip != null)
                RebuildContextMenu(_regionIcon.ContextMenuStrip);
            UpdateRegionTooltip();
        }
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            try
            {
                RebuildContextMenu(menu);
            }
            catch
            {
                LogTrayException("ContextMenu.Opening");
            }
        };
        RebuildContextMenu(menu);
        return menu;
    }

    private void RebuildContextMenu(ContextMenuStrip menu)
    {
        menu.SuspendLayout();
        menu.Items.Clear();
        menu.Items.Add(LocalizationService.T("Tray.MenuOpen"), null, (_, _) => ShowMain());
        if (IsQuickTestRunning())
            menu.Items.Add(LocalizationService.T("Tray.MenuStopQuickTest"), null, (_, _) => StopQuickTest());
        else
            menu.Items.Add(LocalizationService.T("Tray.MenuQuickTest"), null, (_, _) => StartQuickTest(restart: true));
        if (_regionIcon is { Visible: true })
            menu.Items.Add(LocalizationService.T("Tray.MenuCheckRegion"), null, (_, _) => CheckRegionNow());
        menu.Items.Add(LocalizationService.T("Tray.MenuSettings"), null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(LocalizationService.T("Tray.MenuExit"), null, (_, _) => ExitApp());
        menu.ResumeLayout();
    }

    private static bool IsQuickTestRunning() => MeasurementSession.IsRunning;

    // Пункт меню обновится при следующем Opening.
    private static void StopQuickTest() => MeasurementSession.Stop();

    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e) => ScheduleSingleClickIfLeft(e);

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e) => ScheduleSingleClickIfLeft(e);

    private void ScheduleSingleClickIfLeft(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        if (DateTime.UtcNow < _suppressSingleClickScheduleUtc)
            return;
        _singleClickTimer.Interval = TimeSpan.FromMilliseconds(SingleClickDelayMs);
        _singleClickTimer.Stop();
        _singleClickTimer.Start();
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        _singleClickTimer.Stop();
        _suppressSingleClickScheduleUtc = DateTime.UtcNow.AddMilliseconds(SingleClickDelayMs + 100);
        InvokeOnUi(() =>
        {
            try
            {
                ExecuteTrayAction(isDouble: true);
            }
            catch
            {
                LogTrayException("ExecuteTrayAction(double)");
            }
        });
    }

    private static void InvokeOnUi(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null || disp.HasShutdownStarted)
            return;
        if (disp.CheckAccess())
            action();
        else
            disp.BeginInvoke(DispatcherPriority.Input, action);
    }

    private static void InvokeOnUiSync(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null || disp.HasShutdownStarted)
            return;
        if (disp.CheckAccess())
            action();
        else
            disp.Invoke(action, DispatcherPriority.Send);
    }

    private void ExecuteTrayAction(bool isDouble)
    {
        var pairing = SettingsStorage.Load().TrayClickPairing;
        switch (pairing)
        {
            case TrayClickPairing.WindowSingle_QuickTestDouble:
                if (isDouble)
                    StartQuickTest(restart: true);
                else
                    ShowMain();
                break;
            default:
                if (isDouble)
                    ShowMain();
                else
                    StartQuickTest(restart: true);
                break;
        }
    }

    private static Window? GetOwnerWindow()
    {
        return Application.Current.MainWindow is MainWindow mw ? mw : null;
    }

    private static void ShowMain()
    {
        if (Application.Current.MainWindow is not MainWindow mw)
            return;
        try
        {
            mw.ShowFromTray();
        }
        catch
        {
            // Ignore show failures.
        }
    }

    private static void ShowSettings()
    {
        SettingsWindow.ShowSingletonOrActivate(dlg =>
        {
            if (Application.Current.MainWindow is MainWindow mw)
                dlg.LoadSettings(mw.SettingsModel);
            else
                dlg.LoadSettings(SettingsStorage.Load());

            dlg.Owner = null;
            dlg.CenterOnWorkAreaAfterLoad = true;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        });
    }

    private static void ExitApp()
    {
        InvokeOnUiSync(() =>
        {
            App.TryShowAutostartPromptOnFirstExit();
            App.ShutdownRequested = true;
            Application.Current.Shutdown();
        });
    }

    /// <summary>Старт фонового замера в трее при запуске приложения (если включено в настройках), независимо от видимости окна.</summary>
    internal void TryAutoStartOnTrayLaunchIfNeeded(AppSettings settings)
    {
        if (!settings.SpeedTest.AutoStartOnTrayStartup)
            return;
        StartQuickTest(restart: true);
    }

    public void Dispose()
    {
        SettingsStorage.Saved -= OnSettingsSaved;
        MeasurementSession.Started -= OnMeasureStarted;
        MeasurementSession.WaitingForNetwork -= OnMeasureWaiting;
        MeasurementSession.Measuring -= OnMeasureMeasuring;
        MeasurementSession.Progress -= OnMeasureProgress;
        MeasurementSession.Completed -= OnMeasureCompleted;
        MeasurementSession.Aborted -= OnMeasureAborted;
        MeasurementSession.Stop();

        _singleClickTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _iconAnim.Dispose();

        _region.Dispose();
        if (_regionIcon != null)
        {
            _regionIcon.Visible = false;
            _regionIcon.ContextMenuStrip?.Dispose();
            _regionIcon.Dispose();
        }
        _regionAnim?.Dispose();
    }

    // ---------- Иконка региона ----------

    private void OnSettingsSaved(AppSettings settings) => InvokeOnUi(() => ApplyRegionSettings(settings));

    private void ApplyRegionSettings(AppSettings settings)
    {
        var tray = settings.Tray;
        // Страна не показана ни в окне, ни в трее — не проверяем вовсе; при включении — сразу свежая проверка.
        var regionOn = tray.ShowRegionIcon || tray.ShowRegionInWindow;
        // Проверка по таймеру — настройка иконки в трее; без неё страна проверяется при запуске и при смене сети.
        _region.Configure(regionOn, tray.ShowRegionIcon && tray.RegionAutoCheck, tray.RegionCheckIntervalMinutes, tray.RegionEnhanced);
        if (!regionOn)
            _regionStarted = false;
        else if (!_regionStarted)
        {
            _regionStarted = true;
            CheckRegionNow();
        }

        if (!tray.ShowRegionIcon)
        {
            if (_regionIcon != null)
                _regionIcon.Visible = false;
            return;
        }

        if (_regionIcon == null)
        {
            _regionIcon = new NotifyIcon();
            _regionAnim = new TrayIconAnimator(_regionIcon, _dispatcher, BlinkInterval);
            _regionAnim.Show(AppIconFactory.CreateRegionIcon(_region.CountryCode, vpnBadge: _region.VpnDetected));
            _regionIcon.ContextMenuStrip = CreateContextMenu();
            // Левый клик по флагу — проверить регион сейчас.
            _regionIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    CheckRegionNow();
            };
            UpdateRegionTooltip();
        }

        _regionIcon.Visible = true;
    }

    private void CheckRegionNow() => _ = _region.CheckNowAsync();

    /// <summary>Сервисы отвечают за доли секунды — без минимума мигание не успело бы стать заметным.</summary>
    private static readonly TimeSpan RegionBlinkMinDuration = TimeSpan.FromMilliseconds(1200);

    private DateTime _regionCheckStartedUtc;
    private int _regionCheckSeq;

    private void OnRegionCheckStarted()
    {
        if (_regionAnim == null)
            return;
        _regionCheckSeq++;
        _regionCheckStartedUtc = DateTime.UtcNow;
        var code = _region.CountryCode;
        var vpn = _region.VpnDetected;
        // Первый кадр — бледный: реакция на клик видна сразу.
        _regionAnim.Blink(
            "check|" + code + "|" + vpn,
            () => AppIconFactory.CreateRegionIcon(code, dimmed: true, vpnBadge: vpn),
            () => AppIconFactory.CreateRegionIcon(code, vpnBadge: vpn));
        UpdateRegionTooltip();
    }

    private async void OnRegionCheckCompleted()
    {
        if (_regionAnim == null)
            return;
        var seq = _regionCheckSeq;
        var remaining = RegionBlinkMinDuration - (DateTime.UtcNow - _regionCheckStartedUtc);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
        // За время паузы могла начаться новая проверка — тогда её мигание не трогаем.
        if (seq != _regionCheckSeq || _region.IsChecking || _regionAnim == null)
            return;
        _regionAnim.Show(AppIconFactory.CreateRegionIcon(_region.CountryCode, vpnBadge: _region.VpnDetected));
        UpdateRegionTooltip();
    }

    private void UpdateRegionTooltip()
    {
        if (_regionIcon == null)
            return;
        string text;
        if (_region is { IsChecking: true })
            text = LocalizationService.T("Tray.RegionChecking");
        else if (_region.CountryCode is not { } code)
            text = LocalizationService.T("Tray.RegionUnknown");
        else if (_region.VpnDetected && _region.RealCountryCode != null && _region.IpCountryCode != null)
            text = string.Format(CultureInfo.CurrentCulture, LocalizationService.T("Tray.RegionVpn"),
                RegionService.CountryDisplayName(_region.RealCountryCode), RegionService.CountryDisplayName(_region.IpCountryCode));
        else if (_region.RealCheckFailed)
            text = string.Format(CultureInfo.CurrentCulture, LocalizationService.T("Tray.RegionRealUnknown"),
                RegionService.CountryDisplayName(code));
        else
        {
            var name = RegionService.CountryDisplayName(code);
            // RealCountryCode задан только после успешной проверки в усиленном режиме: VPN нет, страна подтверждена.
            var key = _region.LastCheckFailed ? "Tray.RegionCheckFailed"
                : _region.RealCountryCode != null ? "Tray.RegionTooltipEnhanced"
                : "Tray.RegionTooltip";
            text = string.Format(CultureInfo.CurrentCulture, LocalizationService.T(key), name);
        }

        // NotifyIcon.Text ограничен 127 символами.
        _regionIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    // ---------- Быстрый замер ----------

    /// <summary>Замер общий с окном (<see cref="MeasurementSession"/>); трей рисует его на иконке.</summary>
    private void StartQuickTest(bool restart)
    {
        if (!MeasurementSession.Start(restart))
            _notifyIcon.ShowBalloonTip(2500, "ReFlow", LocalizationService.T("Tray.NoServerUrl"), ToolTipIcon.Warning);
    }

    private readonly List<double> _quickTestSamples = new();
    private bool _quickTestMeasuring;

    private void OnMeasureStarted()
    {
        _quickTestSamples.Clear();
        _quickTestMeasuring = false;
        // Реакция на клик сразу, не дожидаясь первого пробника (он может висеть до таймаута).
        BlinkDash(ConnectivityProbe.IsNetworkLikelyDown ? Color.Red : Color.LimeGreen);
    }

    private void OnMeasureWaiting()
    {
        BlinkDash(Color.Red);
        _notifyIcon.Text = LocalizationService.T("Tray.WaitingForNetwork");
    }

    private void OnMeasureMeasuring(TimeSpan duration)
    {
        // Сервер доступен — до первых цифр мигает зелёная черточка.
        _quickTestMeasuring = true;
        BlinkDash(Color.LimeGreen);
        _notifyIcon.Text = LocalizationService.T("Tray.BalloonTip");
    }

    private void OnMeasureCompleted(SpeedTestResult result)
    {
        _quickTestMeasuring = false;
        var peakMbps = _quickTestSamples.Count > 0 ? _quickTestSamples.Max() : result.Download.AverageMbps;
        ShowFinalMaxMbps(peakMbps);
        _notifyIcon.Text = LocalizationService.T("Tray.BalloonTip");
    }

    private void OnMeasureAborted()
    {
        _quickTestMeasuring = false;
        RestoreLastQuickTestGlyphOrBase();
        _notifyIcon.Text = LocalizationService.T("Tray.BalloonTip");
    }

    private static void LogTrayException(string where)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ReFlow");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, DateTime.Now.ToString("O") + " [Tray] " + where + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // best effort
        }
    }

    private void OnMeasureProgress(SpeedTestProgress p)
    {
        // Поздние тики после конца замера не должны перебить итоговую цифру.
        if (!_quickTestMeasuring)
            return;
        if (p.Kind != SpeedTestProgressKind.Rate || p.Mbps is null)
            return;

        _quickTestSamples.Add(p.Mbps.Value);
        var text = Math.Round(p.Mbps.Value).ToString("0");
        // Цифры горят ровно, мигает только полоска по нижней кромке — «замер ещё идёт».
        _iconAnim.Blink(
            "rate|" + text,
            () => AppIconFactory.CreateTrayGlyph(text, Color.LimeGreen, bottomBar: Color.LimeGreen),
            () => AppIconFactory.CreateTrayGlyph(text, Color.LimeGreen));
    }

    /// <summary>Черточка мигает целиком: ждём сеть (красная) или подключаемся (зелёная).</summary>
    private void BlinkDash(Color color)
    {
        _iconAnim.Blink(
            "dash|" + color.ToArgb(),
            () => AppIconFactory.CreateTrayGlyph("–", color),
            () => AppIconFactory.CreateTrayGlyph("–", Color.Black));
    }

    private void ShowFinalMaxMbps(double maxMbps)
    {
        var text = Math.Round(maxMbps).ToString("0");
        _lastQuickTestPeakText = text;
        SetGlyph(text, Color.LimeGreen);
    }

    private void RestoreLastQuickTestGlyphOrBase()
    {
        if (!string.IsNullOrEmpty(_lastQuickTestPeakText))
            SetGlyph(_lastQuickTestPeakText, Color.LimeGreen);
        else
            SetBaseIcon();
    }

    private void SetGlyph(string text, Color color) => _iconAnim.Show(AppIconFactory.CreateTrayGlyph(text, color));

    private void SetBaseIcon() => _iconAnim.Show(AppIconFactory.CreateTrayIcon(_theme));
}
