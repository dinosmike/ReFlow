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

    /// <summary>Последний успешный средний результат (зелёный), держим до следующего клика / перезапуска.</summary>
    private string? _lastQuickTestAvgText;

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

        _notifyIcon.ContextMenuStrip = BuildContextMenu();
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
        var oldMenu = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = BuildContextMenu();
        oldMenu?.Dispose();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var settings = SettingsStorage.Load();

        menu.Items.Add(LocalizationService.T("Tray.MenuOpen"), null, (_, _) => ShowMain());
        menu.Items.Add(LocalizationService.T("Tray.MenuQuickTest"), null, (_, _) => StartQuickTest(restart: true));
        menu.Items.Add(LocalizationService.T("Tray.MenuSettings"), null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(LocalizationService.T("Tray.MenuExit"), null, (_, _) => ExitApp());
        return menu;
    }

    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e) => ScheduleSingleClickIfLeft(e);

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e) => ScheduleSingleClickIfLeft(e);

    private void ScheduleSingleClickIfLeft(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
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

    private void ExecuteTrayAction(bool isDouble)
    {
        if (isDouble)
        {
            // ReFlow: no double-click action for now.
            return;
        }

        var action = SettingsStorage.Load().TraySingleClickAction ?? TrayIconClickAction.QuickTest;
        switch (action)
        {
            case TrayIconClickAction.None:
                break;
            case TrayIconClickAction.OpenWindow:
                ShowMain();
                break;
            case TrayIconClickAction.QuickTest:
                StartQuickTest(restart: true);
                break;
            // Старые значения 2/3 в файле настроек — отдельных действий больше нет.
            case TrayIconClickAction.ToggleResolution:
            case TrayIconClickAction.ShowRandomAdvice:
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
        App.TryShowAutostartPromptOnFirstExit();
        App.ShutdownRequested = true;
        Application.Current.Shutdown();
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
        var fileUrlRaw = (settings.SpeedTestFileUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fileUrlRaw))
        {
            _notifyIcon.ShowBalloonTip(2500, "ReFlow", LocalizationService.T("Tray.NoServerUrl"), ToolTipIcon.Warning);
            return;
        }

        _quickTestCts = new CancellationTokenSource();
        var ct = _quickTestCts.Token;
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
                    var avg = samples.Count > 0 ? samples.Average() : result.Download.AverageMbps;
                    _quickTestUiSealedGeneration = gen;
                    ShowFinalAverage(avg);
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
                });
                LogTrayException("QuickTest");
            }
        }, ct);
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

    private void ShowFinalAverage(double avgMbps)
    {
        var text = Math.Round(avgMbps).ToString("0");
        _lastQuickTestAvgText = text;
        SetGlyph(text, Color.LimeGreen);
    }

    private void RestoreLastQuickTestGlyphOrBase()
    {
        if (!string.IsNullOrEmpty(_lastQuickTestAvgText))
            SetGlyph(_lastQuickTestAvgText, Color.LimeGreen);
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
