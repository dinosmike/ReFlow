using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.IO;
using System;
using System.Windows.Forms;
using ReFlow.Models;
using ReFlow.Services;

namespace ReFlow;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWaitRegistration;
    private TrayService? _tray;

    /// <summary>Истинно при «Выход» из трея — чтобы не отменять закрытие в OnClosing.</summary>
    internal static bool ShutdownRequested { get; set; }

    private static void RunOnUiSync(Action action)
    {
        var disp = Current?.Dispatcher;
        if (disp == null || disp.HasShutdownStarted)
            return;

        if (disp.CheckAccess())
            action();
        else
            disp.Invoke(action, DispatcherPriority.Send);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ReFlow");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "crash.log");
                File.AppendAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + ex.Exception + Environment.NewLine + Environment.NewLine);
            }
            catch { /* best effort */ }

            ex.Handled = true;
        };

        // Catch WinForms thread exceptions (NotifyIcon events).
        try
        {
            System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.ThreadException += (_, ex) =>
            {
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ReFlow");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "crash.log");
                    File.AppendAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + ex.Exception + Environment.NewLine + Environment.NewLine);
                }
                catch { /* best effort */ }
            };
        }
        catch { /* ignore */ }

        // Catch non-UI thread exceptions.
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ReFlow");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "crash.log");
                File.AppendAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + ex.ExceptionObject + Environment.NewLine + Environment.NewLine);
            }
            catch { /* best effort */ }
        };

        // Должен существовать до Mutex, чтобы второй процесс мог сигналить до RegisterWait.
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "ReFlow_ActivateMainWindow");

        _mutex = new Mutex(true, "ReFlow_SingleInstance_Mutex", out var createdNew);
        if (!createdNew)
        {
            activateEvent.Set();
            activateEvent.Dispose();
            Shutdown();
            return;
        }

        _activateEvent = activateEvent;
        _activateWaitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, timedOut) =>
            {
                if (timedOut)
                    return;
                Dispatcher.BeginInvoke(
                    () =>
                    {
                        if (MainWindow is MainWindow mw)
                            mw.ShowFromTray();
                    },
                    DispatcherPriority.Normal);
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);


        var startupSettings = SettingsStorage.Load();
        AutostartService.SetEnabled(startupSettings.AutostartEnabled);

        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, "--simulate-offline", StringComparison.OrdinalIgnoreCase))
            {
                ConnectivityProbe.SimulateOffline = true;
                break;
            }
        }

        // До показа главного окна нельзя использовать OnLastWindowClose: при первом запуске закрытие окна вопроса
        // (советы) сочтётся «последним окном» и процесс завершится до main.Show().
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        ThemeService.Apply(UiTheme.Dark);
        LocalizationService.Apply(AppLanguageCatalog.Normalize(startupSettings.UiLanguage));


        ShutdownMode = ShutdownMode.OnLastWindowClose;
        // Icon не задаём — Windows берёт многомасштабную иконку из exe (ApplicationIcon), как у окна настроек.
        MeasurementSession.Initialize(Dispatcher);
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        if (!ConnectivityProbe.SimulateOffline && startupSettings.MinimizeToTrayOnStartup)
            main.Hide();

        _tray = new TrayService(UiTheme.Dark);
        main.AttachRegion(_tray.Region);
        if (!ConnectivityProbe.SimulateOffline)
            _tray.TryAutoStartOnTrayLaunchIfNeeded(startupSettings);
        LocalizationService.LanguageChanged += OnLocalizationLanguageChanged;
    }

    private void OnLocalizationLanguageChanged() => _tray?.RefreshTexts();

    /// <summary>Пересобрать меню трея (профили, язык, пункты из настроек).</summary>
    internal void RefreshTrayMenu() => _tray?.RefreshTexts();

    /// <summary>Иконка трея (окно использует встроенную в exe иконку).</summary>
    public void RefreshIconsForTheme(UiTheme theme)
    {
        _tray?.RefreshIcon(theme);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_activateWaitRegistration != null && _activateEvent != null)
        {
            _activateWaitRegistration.Unregister(_activateEvent);
            _activateWaitRegistration = null;
        }

        _activateEvent?.Dispose();
        _activateEvent = null;

        LocalizationService.LanguageChanged -= OnLocalizationLanguageChanged;
        _tray?.Dispose();
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // ignored
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Если askOnExit == true — показать вопрос про автозапуск. Вызывать ДО Shutdown().</summary>
    internal static void TryShowAutostartPromptOnFirstExit()
    {
        try
        {
            RunOnUiSync(() =>
            {
                var s = SettingsStorage.Load();
                if (!s.AskOnExit)
                    return;

                var dlg = new AutostartPromptWindow();

                // Must be shown on the WPF UI thread; also ensure it's not hidden behind the main window.
                var owner = Current?.MainWindow;
                if (owner != null)
                {
                    dlg.Owner = owner;
                    dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    dlg.ShowInTaskbar = false;
                }
                else
                {
                    dlg.Owner = null;
                    dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    dlg.ShowInTaskbar = true;
                }

                dlg.ShowActivated = true;
                dlg.Topmost = true;
                try
                {
                    dlg.ShowDialog();
                }
                finally
                {
                    dlg.Topmost = false;
                }
            });
        }
        catch
        {
            // не мешаем завершению процесса
        }
    }

}
