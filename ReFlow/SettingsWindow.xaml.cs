using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ReFlow.Models;
using ReFlow.Services;

namespace ReFlow;

public partial class SettingsWindow
{
    private AppSettings _settings = null!;
    private bool _languageComboLoading;
    private string _languageAtOpen = "en";

    private const string SpeedTestUrlHistoryDeleteTag = "__delete__";

    private void RemoveUrlFromHistoryAndPersist(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        _settings.SpeedTest.DownloadFileUrlHistory.RemoveAll(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase));
        SettingsStorage.Save(_settings);

        if (!HasSavedUrlHistory())
        {
            SpeedTestUrlHistoryPopup?.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
            return;
        }

        if (SpeedTestUrlHistoryPopup?.IsOpen == true)
            FillSpeedTestUrlHistoryList();
    }

    private bool FillSpeedTestUrlHistoryList()
    {
        if (SpeedTestUrlHistoryList == null)
            return false;

        SpeedTestUrlHistoryList.Items.Clear();
        var iconColW = 22.0;
        var gap = 8.0;
        var accent = TryFindResource("Rf.Accent") as System.Windows.Media.Brush;
        var muted = TryFindResource("Rf.Text.Muted") as System.Windows.Media.Brush;
        var primary = TryFindResource("Rf.Text.Primary") as System.Windows.Media.Brush;

        var history = _settings?.SpeedTest?.DownloadFileUrlHistory ?? new();
        var items = history
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (items.Count == 0)
            return false;

        foreach (var url in items)
        {
            var row = new ListBoxItem
            {
                Tag = url,
                Focusable = false,
                IsTabStop = false,
                Padding = new Thickness(6, 0, 6, 0)
            };

            var deleteGlyph = new TextBlock
            {
                // Use the same MDL2 wiring as the rest of the UI (see Themes/Dark.xaml: Rf.TextBlock.ProfileActionIcon).
                Style = (Style)FindResource("Rf.TextBlock.ProfileActionIcon"),
                Text = "\uE711", // Cancel (MDL2)
                Margin = new Thickness(0),
                Foreground = muted,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            var deleteHost = new Border
            {
                Tag = SpeedTestUrlHistoryDeleteTag,
                Width = iconColW,
                Height = iconColW,
                CornerRadius = new CornerRadius(4),
                Background = System.Windows.Media.Brushes.Transparent,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = LocalizationService.T("SettingsWindow.RemoveSpeedTestUrlFromHistory")
            };
            deleteHost.Child = deleteGlyph;
            deleteHost.MouseEnter += (_, _) =>
            {
                if (accent != null)
                    deleteGlyph.Foreground = accent;
            };
            deleteHost.MouseLeave += (_, _) =>
            {
                if (muted != null)
                    deleteGlyph.Foreground = muted;
            };

            var urlText = new TextBlock
            {
                Text = url,
                Foreground = primary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(gap, 0, 0, 0),
                ToolTip = url
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconColW) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(deleteHost);
            grid.Children.Add(urlText);
            Grid.SetColumn(deleteHost, 0);
            Grid.SetColumn(urlText, 1);
            row.Content = grid;

            SpeedTestUrlHistoryList.Items.Add(row);
        }

        SpeedTestUrlHistoryList.Items.Add(new Separator());

        var clearGlyph = new TextBlock
        {
            Style = (Style)FindResource("Rf.TextBlock.ProfileActionIcon"),
            Text = "\uE74D", // Delete / Trash (MDL2)
            Margin = new Thickness(0),
            Foreground = muted,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var clearIconHost = new Border
        {
            Width = iconColW,
            Height = iconColW,
            Background = System.Windows.Media.Brushes.Transparent,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            Child = clearGlyph
        };
        var clearText = new TextBlock
        {
            Text = LocalizationService.T("SettingsWindow.ClearSpeedTestUrlHistory"),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Foreground = primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(gap, 0, 0, 0),
            ToolTip = LocalizationService.T("SettingsWindow.ClearSpeedTestUrlHistory")
        };
        var clearGrid = new Grid();
        clearGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconColW) });
        clearGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        clearGrid.Children.Add(clearIconHost);
        clearGrid.Children.Add(clearText);
        Grid.SetColumn(clearIconHost, 0);
        Grid.SetColumn(clearText, 1);

        var clearItem = new ListBoxItem
        {
            Tag = "__clear__",
            Focusable = false,
            IsTabStop = false,
            Content = clearGrid
        };
        SpeedTestUrlHistoryList.Items.Add(clearItem);

        return true;
    }

    private static bool IsDescendantOf(DependencyObject? ancestor, DependencyObject? node)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private bool HasSavedUrlHistory() =>
        _settings?.SpeedTest?.DownloadFileUrlHistory?.Any(x => !string.IsNullOrWhiteSpace(x)) == true;

    private void SettingsWindow_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SpeedTestUrlHistoryPopup?.IsOpen != true)
            return;

        if (e.OriginalSource is not DependencyObject src)
            return;

        if (IsDescendantOf(SpeedTestUrlBox, src) || IsDescendantOf(SpeedTestUrlHistoryPopup, src))
            return;

        SpeedTestUrlHistoryPopup.IsOpen = false;
    }

    private void SpeedTestUrlHistoryPopup_OnClosed(object? sender, EventArgs e)
    {
        PreviewMouseDown -= SettingsWindow_OnPreviewMouseDown;
    }

    private void TryToggleSpeedTestUrlHistoryPopup()
    {
        if (SpeedTestUrlHistoryPopup == null || SpeedTestUrlBox == null)
            return;
        if (UseCustomSpeedTestUrlCheck?.IsChecked != true)
            return;

        if (SpeedTestUrlHistoryPopup.IsOpen)
        {
            SpeedTestUrlHistoryPopup.IsOpen = false;
            return;
        }

        if (!HasSavedUrlHistory())
            return;

        if (!FillSpeedTestUrlHistoryList())
            return;

        PreviewMouseDown -= SettingsWindow_OnPreviewMouseDown;
        PreviewMouseDown += SettingsWindow_OnPreviewMouseDown;

        SpeedTestUrlHistoryPopup.IsOpen = true;
    }

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
        Deactivated += SettingsWindow_OnDeactivated;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            RevertAndClose();
        };
    }

    private void SettingsWindow_OnDeactivated(object? sender, EventArgs e)
    {
        if (SpeedTestUrlHistoryPopup?.IsOpen == true)
            SpeedTestUrlHistoryPopup.IsOpen = false;
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
        {
            SpeedTestUrlBox.Text = _settings.SpeedTest.DownloadFileUrl ?? "";
        }
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

        ShowRegionIconCheck.IsChecked = settings.Tray.ShowRegionIcon;
        RegionAutoCheckCheck.IsChecked = settings.Tray.RegionAutoCheck;
        RegionEnhancedCheck.IsChecked = settings.Tray.RegionEnhanced;
        RegionIntervalBox.Text = settings.Tray.RegionCheckIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        UpdateRegionOptionsEnabled();
    }

    private void RegionOptions_OnChanged(object sender, RoutedEventArgs e) => UpdateRegionOptionsEnabled();

    /// <summary>Периодичность имеет смысл только при включённой иконке, интервал — только при включённой периодичности.</summary>
    private void UpdateRegionOptionsEnabled()
    {
        if (ShowRegionIconCheck == null || RegionAutoCheckCheck == null || RegionIntervalRow == null || RegionEnhancedCheck == null)
            return;
        var iconOn = ShowRegionIconCheck.IsChecked == true;
        RegionEnhancedCheck.IsEnabled = iconOn;
        RegionAutoCheckCheck.IsEnabled = iconOn;
        RegionIntervalRow.IsEnabled = iconOn && RegionAutoCheckCheck.IsChecked == true;
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
            var normalized = string.IsNullOrWhiteSpace(rawUrl) ? null : rawUrl;
            _settings.SpeedTest.DownloadFileUrl = normalized;
            var wantCustom = UseCustomSpeedTestUrlCheck?.IsChecked == true;
            _settings.SpeedTest.UseCustomDownloadFileUrl = wantCustom && _settings.SpeedTest.DownloadFileUrl != null;

            if (_settings.SpeedTest.UseCustomDownloadFileUrl && _settings.SpeedTest.DownloadFileUrl != null)
            {
                var url = _settings.SpeedTest.DownloadFileUrl;
                _settings.SpeedTest.DownloadFileUrlHistory.RemoveAll(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase));
                _settings.SpeedTest.DownloadFileUrlHistory.Insert(0, url);
                if (_settings.SpeedTest.DownloadFileUrlHistory.Count > 15)
                    _settings.SpeedTest.DownloadFileUrlHistory.RemoveRange(15, _settings.SpeedTest.DownloadFileUrlHistory.Count - 15);
            }
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

        _settings.Tray.ShowRegionIcon = ShowRegionIconCheck.IsChecked == true;
        _settings.Tray.RegionAutoCheck = RegionAutoCheckCheck.IsChecked == true;
        _settings.Tray.RegionEnhanced = RegionEnhancedCheck.IsChecked == true;
        if (int.TryParse(RegionIntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var regionMin) && regionMin >= 1)
            _settings.Tray.RegionCheckIntervalMinutes = Math.Min(regionMin, 1440);

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
        if (!enabled && SpeedTestUrlHistoryPopup != null)
            SpeedTestUrlHistoryPopup.IsOpen = false;
    }

    private void SpeedTestUrlBox_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Defer opening so TextBox keeps caret/focus normally.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TryToggleSpeedTestUrlHistoryPopup();
        }), DispatcherPriority.Input);
    }

    private void SpeedTestUrlHistoryList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SpeedTestUrlHistoryPopup == null || SpeedTestUrlBox == null)
            return;

        DependencyObject? cur = e.OriginalSource as DependencyObject;
        FrameworkElement? deleteHit = null;
        while (cur != null && cur is not ListBoxItem)
        {
            if (cur is FrameworkElement fe && string.Equals(fe.Tag as string, SpeedTestUrlHistoryDeleteTag, StringComparison.Ordinal))
                deleteHit = fe;
            cur = VisualTreeHelper.GetParent(cur);
        }

        if (cur is not ListBoxItem item)
            return;

        e.Handled = true;

        if (deleteHit != null)
        {
            if (item.Tag is string url && !string.IsNullOrWhiteSpace(url))
                RemoveUrlFromHistoryAndPersist(url);
            SpeedTestUrlBox.Focus();
            return;
        }

        SpeedTestUrlHistoryPopup.IsOpen = false;

        if (item.Tag is string tag && tag == "__clear__")
        {
            _settings.SpeedTest.DownloadFileUrlHistory.Clear();
            SettingsStorage.Save(_settings);
            SpeedTestUrlBox.Focus();
            return;
        }

        if (item.Tag is string pickedUrl && !string.IsNullOrWhiteSpace(pickedUrl))
        {
            SpeedTestUrlBox.Text = pickedUrl;
            SpeedTestUrlBox.CaretIndex = SpeedTestUrlBox.Text?.Length ?? 0;
        }

        SpeedTestUrlBox.Focus();
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

    private void RegionIntervalUp_OnClick(object sender, RoutedEventArgs e) => BumpRegionIntervalMinutes(1);

    private void RegionIntervalDown_OnClick(object sender, RoutedEventArgs e) => BumpRegionIntervalMinutes(-1);

    private void BumpRegionIntervalMinutes(int delta)
    {
        if (!int.TryParse(RegionIntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            v = _settings.Tray.RegionCheckIntervalMinutes;
        v = Math.Clamp(v + delta, 1, 1440);
        RegionIntervalBox.Text = v.ToString(CultureInfo.InvariantCulture);
    }
}
