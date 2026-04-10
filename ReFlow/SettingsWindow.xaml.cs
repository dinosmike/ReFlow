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

        FileUrlBox.Text = settings.SpeedTestFileUrl ?? string.Empty;
        DurationSecondsBox.Text = settings.SpeedTestDurationSeconds.ToString();

        var action = settings.TraySingleClickAction ?? TrayIconClickAction.QuickTest;
        switch (action)
        {
            case TrayIconClickAction.QuickTest:
                TraySingleQuickTest.IsChecked = true;
                break;
            case TrayIconClickAction.None:
                TraySingleNone.IsChecked = true;
                break;
            default:
                TraySingleOpenWindow.IsChecked = true;
                break;
        }
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (TraySingleQuickTest.IsChecked == true)
            _settings.TraySingleClickAction = TrayIconClickAction.QuickTest;
        else if (TraySingleNone.IsChecked == true)
            _settings.TraySingleClickAction = TrayIconClickAction.None;
        else
            _settings.TraySingleClickAction = TrayIconClickAction.OpenWindow;

        _settings.SpeedTestFileUrl = FileUrlBox.Text.Trim();
        if (!int.TryParse(DurationSecondsBox.Text.Trim(), out var sec) || sec < 1)
            sec = 15;
        if (sec > 600)
            sec = 600;
        _settings.SpeedTestDurationSeconds = sec;

        _settings.UiLanguage = AppLanguageCatalog.Normalize(LanguageCombo.SelectedValue as string ?? _settings.UiLanguage);

        SettingsStorage.Save(_settings);
        DialogResult = true;
        Close();
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
}
