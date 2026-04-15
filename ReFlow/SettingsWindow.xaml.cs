using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReFlow.Models;
using ReFlow.Services;

namespace ReFlow;

public partial class SettingsWindow
{
    private AppSettings _settings = null!;
    private bool _languageComboLoading;
    private string _languageAtOpen = "en";

    private void SetDurationChoiceFromSettings()
    {
        // Ручное поле всегда показывает последнее ручное значение, независимо от выбранного пресета.
        var manual = Math.Clamp(_settings.SpeedTest.ManualDurationSeconds, 1, 600);
        DurationSecondsBox.Text = manual.ToString(CultureInfo.InvariantCulture);

        var sec = Math.Clamp(_settings.SpeedTestDurationSeconds, 1, 600);
        if (sec == 10)
            DurationPreset10.IsChecked = true;
        else if (sec == 30)
            DurationPreset30.IsChecked = true;
        else if (sec == 60)
            DurationPreset60.IsChecked = true;
        else
        {
            DurationManual.IsChecked = true;
            // Если сейчас выбран ручной режим, показываем текущее значение замера (оно и есть ручное).
            DurationSecondsBox.Text = sec.ToString(CultureInfo.InvariantCulture);
        }

        UpdateManualDurationEnabled();
    }

    private void UpdateManualDurationEnabled()
    {
        var enabled = DurationManual.IsChecked == true;
        DurationManualBox.IsEnabled = enabled;
        DurationUpBtn.IsEnabled = enabled;
        DurationDownBtn.IsEnabled = enabled;
        DurationSecondsBox.IsReadOnly = !enabled;
        DurationSecondsBox.Focusable = enabled;

        var foreKey = enabled ? "Rf.Text.Primary" : "Rf.Text.Muted";
        if (TryFindResource(foreKey) is System.Windows.Media.Brush b)
        {
            DurationSecondsBox.Foreground = b;
            DurationUpBtn.Foreground = b;
            DurationDownBtn.Foreground = b;
        }
    }

    /// <summary>Если true — после первой вёрстки центрировать по рабочей области экрана (трей, <see cref="SizeToContent"/>).</summary>
    internal bool CenterOnWorkAreaAfterLoad { get; set; }

    private static SettingsWindow? _singletonInstance;

    /// <summary>Одно модальное окно настроек: повторный вызов активирует уже открытое (трей / главное окно).</summary>
    internal static void ShowSingletonOrActivate(Action<SettingsWindow> configure)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null)
            return;

        void Run()
        {
            if (_singletonInstance != null)
            {
                _singletonInstance.Activate();
                if (_singletonInstance.WindowState == WindowState.Minimized)
                    _singletonInstance.WindowState = WindowState.Normal;
                _singletonInstance.Focus();
                return;
            }

            var dlg = new SettingsWindow();
            configure(dlg);
            _singletonInstance = dlg;
            try
            {
                if (dlg.ShowDialog() == true && app.MainWindow is MainWindow main)
                    main.ReloadFromStorage();
            }
            finally
            {
                if (_singletonInstance == dlg)
                    _singletonInstance = null;
            }
        }

        if (app.Dispatcher.CheckAccess())
            Run();
        else
            app.Dispatcher.Invoke(Run);
    }

    public SettingsWindow()
    {
        InitializeComponent();
        TooltipDelayHelper.Apply(this, MainWindow.TooltipInitialShowDelayMs);

        Loaded += OnLoaded;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            RevertAndClose();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (CenterOnWorkAreaAfterLoad)
            CenterOnWorkArea();

        // Не ставим каретку в ручное поле длительности при открытии окна.
        // Фокус — на первый чекбокс, чтобы курсор нигде не мигал, пока пользователь не кликнет сам.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Keyboard.ClearFocus();
                AutostartCheck.Focus();
            }
            catch
            {
                // ignore
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CenterOnWorkArea()
    {
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + (wa.Height - ActualHeight) / 2;
    }

    public void LoadSettings(AppSettings settings)
    {
        _settings = settings;
        _languageAtOpen = AppLanguageCatalog.Normalize(settings.UiLanguage);

        _languageComboLoading = true;
        LanguageCombo.ItemsSource = AppLanguageCatalog.All;
        LanguageCombo.SelectedValue = _languageAtOpen;
        _languageComboLoading = false;

        SetDurationChoiceFromSettings();

        if (SpeedTestUrlBox != null)
            SpeedTestUrlBox.Text = _settings.SpeedTest.DownloadFileUrl ?? "";
        if (UseCustomSpeedTestUrlCheck != null)
            UseCustomSpeedTestUrlCheck.IsChecked = _settings.SpeedTest.UseCustomDownloadFileUrl;
        UpdateCustomSpeedTestUrlEnabled();

        AutostartCheck.IsChecked = settings.AutostartEnabled;
        StartupToTrayCheck.IsChecked = settings.MinimizeToTrayOnStartup;
        TrayHideAnimationCheck.IsChecked = settings.MinimizeToTrayAnimationEnabled;
        CloseToTrayCheck.IsChecked = settings.MinimizeToTrayOnCloseClick;

        AutoMeasureOnWindowOpenCheck.IsChecked = settings.SpeedTest.AutoStartWhenWindowOpen;
        AutoMeasureTrayStartupCheck.IsChecked = settings.SpeedTest.AutoStartOnTrayStartup;

        if (settings.TrayClickPairing == TrayClickPairing.WindowSingle_QuickTestDouble)
            TrayPairOpenSingle.IsChecked = true;
        else
            TrayPairQuickSingle.IsChecked = true;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.TrayClickPairing = TrayPairOpenSingle.IsChecked == true
            ? TrayClickPairing.WindowSingle_QuickTestDouble
            : TrayClickPairing.QuickTestSingle_WindowDouble;
        _settings.TraySingleClickAction = _settings.TrayClickPairing == TrayClickPairing.WindowSingle_QuickTestDouble
            ? TrayIconClickAction.OpenWindow
            : TrayIconClickAction.QuickTest;

        int sec;
        if (DurationPreset10.IsChecked == true)
            sec = 10;
        else if (DurationPreset30.IsChecked == true)
            sec = 30;
        else if (DurationPreset60.IsChecked == true)
            sec = 60;
        else if (!int.TryParse(DurationSecondsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sec) || sec < 1)
            sec = Math.Clamp(_settings.SpeedTest.ManualDurationSeconds, 1, 600);
        if (sec > 600)
            sec = 600;
        _settings.SpeedTestDurationSeconds = sec;
        if (DurationManual.IsChecked == true)
            _settings.SpeedTest.ManualDurationSeconds = sec;
        else if (int.TryParse(DurationSecondsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var manualSec) && manualSec >= 1)
            _settings.SpeedTest.ManualDurationSeconds = Math.Clamp(manualSec, 1, 600);

        if (SpeedTestUrlBox != null)
        {
            var rawUrl = SpeedTestUrlBox.Text?.Trim() ?? "";
            _settings.SpeedTest.DownloadFileUrl = string.IsNullOrWhiteSpace(rawUrl) ? null : rawUrl;
            var wantCustom = UseCustomSpeedTestUrlCheck?.IsChecked == true;
            _settings.SpeedTest.UseCustomDownloadFileUrl = wantCustom && _settings.SpeedTest.DownloadFileUrl != null;
        }
        else
        {
            _settings.SpeedTest.UseCustomDownloadFileUrl = false;
        }

        _settings.AutostartEnabled = AutostartCheck.IsChecked == true;
        _settings.MinimizeToTrayOnStartup = StartupToTrayCheck.IsChecked == true;
        _settings.MinimizeToTrayAnimationEnabled = TrayHideAnimationCheck.IsChecked == true;
        _settings.MinimizeToTrayOnCloseClick = CloseToTrayCheck.IsChecked == true;

        _settings.SpeedTest.AutoStartWhenWindowOpen = AutoMeasureOnWindowOpenCheck.IsChecked == true;
        _settings.SpeedTest.AutoStartOnTrayStartup = AutoMeasureTrayStartupCheck.IsChecked == true;

        AutostartService.SetEnabled(_settings.AutostartEnabled);

        _settings.UiLanguage = AppLanguageCatalog.Normalize(LanguageCombo.SelectedValue as string ?? _settings.UiLanguage);

        SettingsStorage.Save(_settings);
        DialogResult = true;
        Close();
    }

    private void UseCustomSpeedTestUrlCheck_OnChecked(object sender, RoutedEventArgs e) => UpdateCustomSpeedTestUrlEnabled();

    private void UpdateCustomSpeedTestUrlEnabled()
    {
        var enabled = UseCustomSpeedTestUrlCheck?.IsChecked == true;
        if (SpeedTestUrlBox != null)
        {
            SpeedTestUrlBox.IsEnabled = enabled;
            SpeedTestUrlBox.IsReadOnly = !enabled;
        }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => RevertAndClose();

    /// <summary>Откат темы и языка (язык мог быть записан в файл при мгновенном выборе).</summary>
    private void RevertAndClose()
    {
        _settings.UiLanguage = _languageAtOpen;
        SettingsStorage.Save(_settings);
        LocalizationService.Apply(_languageAtOpen);
        DialogResult = false;
        Close();
    }

    private void LanguageCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languageComboLoading)
            return;
        if (LanguageCombo.SelectedValue is not string code)
            return;

        LocalizationService.Apply(code);
        _settings.UiLanguage = code;
        SettingsStorage.Save(_settings);
    }

    private void TitleBar_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void TitleBarClose_OnClick(object sender, RoutedEventArgs e)
    {
        Cancel_OnClick(sender, e);
    }

    private void TitleBarAbout_OnClick(object sender, RoutedEventArgs e) => About.Show(this);

    private void TitleBarAbout_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        About.Show(this);
    }

    private void DurationUp_OnClick(object sender, RoutedEventArgs e) => BumpDurationSeconds(1);

    private void DurationDown_OnClick(object sender, RoutedEventArgs e) => BumpDurationSeconds(-1);

    private void DurationPreset10_OnChecked(object sender, RoutedEventArgs e)
    {
        UpdateManualDurationEnabled();
    }

    private void DurationPreset30_OnChecked(object sender, RoutedEventArgs e)
    {
        UpdateManualDurationEnabled();
    }

    private void DurationPreset60_OnChecked(object sender, RoutedEventArgs e)
    {
        UpdateManualDurationEnabled();
    }

    private void DurationManual_OnChecked(object sender, RoutedEventArgs e)
    {
        UpdateManualDurationEnabled();
    }

    private void DurationSecondsBox_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DurationManual.IsChecked != true)
            DurationManual.IsChecked = true;
    }

    private void DurationSecondsBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DurationManual.IsChecked != true)
            DurationManual.IsChecked = true;
    }

    private void BumpDurationSeconds(int delta)
    {
        if (DurationManual.IsChecked != true)
            DurationManual.IsChecked = true;

        if (!int.TryParse(DurationSecondsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            v = 10;
        v = Math.Clamp(v + delta, 1, 600);
        DurationSecondsBox.Text = v.ToString(CultureInfo.InvariantCulture);
    }
}
