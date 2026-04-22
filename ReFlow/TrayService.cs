using System.Drawing;
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

    private readonly NotifyIcon _notifyIcon;
    /// <summary>WPF-диспетчер: WinForms <see cref="System.Windows.Forms.Timer"/> в чистом WPF часто не получает тики.</summary>
    private readonly DispatcherTimer _singleClickTimer;
    private readonly UiTheme _theme;

    private readonly HttpClient _http = new();
    private readonly SpeedTestService _speedTest;

    private CancellationTokenSource? _quickTestCts;

    /// <summary>Поколение запуска быстрого теста — игнорировать колбэки от отменённой задачи.</summary>
    private int _quickTestGeneration;

    /// <summary>UI для этого поколения уже финализирован (зелёный/восстановление) — игнорировать поздние Rate из очереди Dispatcher.</summary>
    private int _quickTestUiSealedGeneration;

    /// <summary>Последний успешный пик (макс. скорость по сэмплам, зелёный), держим до следующего клика / перезапуска.</summary>
    private string? _lastQuickTestPeakText;

    /// <summary>После <see cref="OnMouseDoubleClick"/> приходит ещё MouseClick/MouseUp по второму клику — иначе они снова стартуют таймер и открывают окно.</summary>
    private DateTime _suppressSingleClickScheduleUtc;

    public TrayService(UiTheme theme)
    {
        _theme = theme;
        _speedTest = new SpeedTestService(_http);

        var disp = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Application.Current is null.");
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
            Icon = AppIconFactory.CreateTrayIcon(theme),
            Text = LocalizationService.T("Tray.BalloonTip"),
            Visible = true
        };
        // MouseUp + MouseClick: в разных версиях Windows для NotifyIcon приходит не всегда одно и то же.
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;

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
        _notifyIcon.ContextMenuStrip = menu;
    }

    /// <summary>Перерисовать иконку трея под выбранную тему (цвета как в UI).</summary>
    public void RefreshIcon(UiTheme theme)
    {
        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = AppIconFactory.CreateTrayIcon(theme);
        old?.Dispose();
    }

    public void RefreshTexts()
    {
        _notifyIcon.Text = LocalizationService.T("Tray.BalloonTip");
        if (_notifyIcon.ContextMenuStrip != null)
            RebuildContextMenu(_notifyIcon.ContextMenuStrip);
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
        menu.Items.Add(LocalizationService.T("Tray.MenuSettings"), null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(LocalizationService.T("Tray.MenuExit"), null, (_, _) => ExitApp());
        menu.ResumeLayout();
    }

    private bool IsQuickTestRunning() => _quickTestCts is { IsCancellationRequested: false };

    private void StopQuickTest()
    {
        try { _quickTestCts?.Cancel(); } catch { /* ignore */ }

        // Пункт меню обновится при следующем Opening.
    }

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
        try { _quickTestCts?.Cancel(); } catch { /* ignore */ }
        _quickTestCts?.Dispose();
        _http.Dispose();

        _singleClickTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    private void StartQuickTest(bool restart)
    {
        if (restart)
        {
            try { _quickTestCts?.Cancel(); } catch { /* ignore */ }
        }

        if (_quickTestCts is { IsCancellationRequested: false })
            return;

        var settings = SettingsStorage.Load();
        var fileUrlRaw = ((settings.SpeedTest.UseCustomDownloadFileUrl ? settings.SpeedTest.DownloadFileUrl : null) ?? SpeedTestConfig.DownloadFileUrl).Trim();
        if (string.IsNullOrWhiteSpace(fileUrlRaw))
        {
            _notifyIcon.ShowBalloonTip(2500, "ReFlow", LocalizationService.T("Tray.NoServerUrl"), ToolTipIcon.Warning);
            return;
        }

        var cts = new CancellationTokenSource();
        _quickTestCts = cts;
        var ct = cts.Token;
        var gen = ++_quickTestGeneration;
        var samples = new List<double>();

        InvokeOnUi(() => SetBaseIcon());

        _ = Task.Run(async () =>
        {
            try
            {
                var dlBase = new Uri(fileUrlRaw, UriKind.Absolute);

                var duration = TimeSpan.FromSeconds(Math.Clamp(settings.SpeedTestDurationSeconds, 1, 600));
                var options = new SpeedTestOptions
                {
                    Duration = duration,
                    ParallelStreams = 4,
                    DownloadUrlBuilder = () => new Uri(dlBase + (dlBase.Query.Length > 0 ? "&" : "?") + "r=" + Guid.NewGuid().ToString("N"))
                };

                var prog = new Progress<SpeedTestProgress>(p =>
                    InvokeOnUi(() => OnQuickTestProgress(gen, samples, p)));
                var result = await _speedTest.RunAsync(options, prog, ct).ConfigureAwait(false);

                InvokeOnUi(() =>
                {
                    if (gen != _quickTestGeneration)
                        return;
                    var peakMbps = samples.Count > 0 ? samples.Max() : result.Download.AverageMbps;
                    _quickTestUiSealedGeneration = gen;
                    ShowFinalMaxMbps(peakMbps);
                    FinalizeQuickTestIfCurrent(gen, cts);
                });
            }
            catch (OperationCanceledException)
            {
                InvokeOnUi(() =>
                {
                    if (gen != _quickTestGeneration)
                        return;
                    _quickTestUiSealedGeneration = gen;
                    RestoreLastQuickTestGlyphOrBase();
                    FinalizeQuickTestIfCurrent(gen, cts);
                });
            }
            catch
            {
                InvokeOnUi(() =>
                {
                    if (gen != _quickTestGeneration)
                        return;
                    _quickTestUiSealedGeneration = gen;
                    RestoreLastQuickTestGlyphOrBase();
                    FinalizeQuickTestIfCurrent(gen, cts);
                });
                LogTrayException("QuickTest");
            }
        }, ct);
    }

    private void FinalizeQuickTestIfCurrent(int gen, CancellationTokenSource cts)
    {
        if (gen != _quickTestGeneration)
            return;
        if (!ReferenceEquals(_quickTestCts, cts))
            return;

        _quickTestCts = null;
        try { cts.Dispose(); } catch { /* ignore */ }
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

    private void OnQuickTestProgress(int gen, List<double> samples, SpeedTestProgress p)
    {
        if (gen != _quickTestGeneration)
            return;
        if (_quickTestUiSealedGeneration == gen)
            return;
        if (p.Kind != SpeedTestProgressKind.Rate || p.Mbps is null)
            return;

        samples.Add(p.Mbps.Value);
        var text = Math.Round(p.Mbps.Value).ToString("0");
        SetGlyph(text, Color.Red);
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

    private void SetGlyph(string text, Color color)
    {
        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = AppIconFactory.CreateTrayGlyph(text, color);
        old?.Dispose();
    }

    private void SetBaseIcon()
    {
        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = AppIconFactory.CreateTrayIcon(_theme);
        old?.Dispose();
    }
}
