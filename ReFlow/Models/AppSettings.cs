using System.Text.Json.Serialization;
using ReFlow.Services;

namespace ReFlow.Models;

/// <summary>ReFlow_settings.json: <c>startupAndWindow</c>, <c>tray</c>, <c>ui</c>, <c>meta</c>, <c>speedTest</c>.</summary>
public sealed class AppSettings
{
    [JsonPropertyOrder(0)]
    [JsonPropertyName("startupAndWindow")]
    public StartupWindowSettings StartupAndWindow { get; set; } = new();

    [JsonPropertyOrder(1)]
    [JsonPropertyName("tray")]
    public TrayMenuSettings Tray { get; set; } = new();

    [JsonPropertyOrder(2)]
    [JsonPropertyName("ui")]
    public UiAppearanceSettings Ui { get; set; } = new();

    [JsonPropertyOrder(3)]
    [JsonPropertyName("meta")]
    public SettingsMeta Meta { get; set; } = new();

    [JsonPropertyOrder(4)]
    [JsonPropertyName("speedTest")]
    public SpeedTestSettings SpeedTest { get; set; } = new();

    [JsonIgnore]
    public bool AutostartEnabled
    {
        get => StartupAndWindow.AutostartEnabled;
        set => StartupAndWindow.AutostartEnabled = value;
    }

    [JsonIgnore]
    public bool MinimizeToTrayOnCloseClick
    {
        get => StartupAndWindow.MinimizeToTrayOnCloseClick;
        set => StartupAndWindow.MinimizeToTrayOnCloseClick = value;
    }

    [JsonIgnore]
    public bool MinimizeToTrayOnStartup
    {
        get => StartupAndWindow.MinimizeToTrayOnStartup;
        set => StartupAndWindow.MinimizeToTrayOnStartup = value;
    }

    [JsonIgnore]
    public bool MinimizeToTrayAnimationEnabled
    {
        get => StartupAndWindow.MinimizeToTrayAnimationEnabled;
        set => StartupAndWindow.MinimizeToTrayAnimationEnabled = value;
    }

    [JsonIgnore]
    public TrayIconClickAction? TraySingleClickAction
    {
        get => Tray.SingleClickAction;
        set => Tray.SingleClickAction = value;
    }

    [JsonIgnore]
    public TrayClickPairing TrayClickPairing
    {
        get => Tray.ClickPairing;
        set => Tray.ClickPairing = value;
    }

    [JsonIgnore]
    public UiTheme UiTheme
    {
        get => Ui.Theme;
        set => Ui.Theme = value;
    }

    [JsonIgnore]
    public string? UiLanguage
    {
        get => Ui.Language;
        set => Ui.Language = value;
    }

    [JsonIgnore]
    public bool AskOnExit
    {
        get => StartupAndWindow.AskOnExit;
        set => StartupAndWindow.AskOnExit = value;
    }

    [JsonIgnore]
    public int SettingsFormatVersion
    {
        get => Meta.SettingsFormatVersion;
        set => Meta.SettingsFormatVersion = value;
    }

    [JsonIgnore]
    public int SpeedTestDurationSeconds
    {
        get => SpeedTest.DurationSeconds;
        set => SpeedTest.DurationSeconds = value;
    }

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            StartupAndWindow = new StartupWindowSettings
            {
                AutostartEnabled = false,
                MinimizeToTrayOnCloseClick = false,
                MinimizeToTrayOnStartup = false,
                MinimizeToTrayAnimationEnabled = true
            },
            Tray = new TrayMenuSettings
            {
                ClickPairing = TrayClickPairing.WindowSingle_QuickTestDouble,
                SingleClickAction = TrayIconClickAction.OpenWindow
            },
            Ui = new UiAppearanceSettings
            {
                Theme = UiTheme.Dark,
                Language = AppLanguageCatalog.ResolveDefaultLanguage()
            },
            Meta = new SettingsMeta { SettingsFormatVersion = 9 },
            SpeedTest = new SpeedTestSettings
            {
                DurationSeconds = 15,
                AutoStartWhenWindowOpen = false,
                AutoStartOnTrayStartup = true
            }
        };
    }

    internal static AppSettings FromFlatLegacy(AppSettingsFlatLegacy src)
    {
        return new AppSettings
        {
            StartupAndWindow = new StartupWindowSettings
            {
                AutostartEnabled = src.AutostartEnabled,
                MinimizeToTrayOnCloseClick = src.MinimizeToTrayOnCloseClick,
                MinimizeToTrayOnStartup = src.MinimizeToTrayOnStartup,
                MinimizeToTrayAnimationEnabled = src.MinimizeToTrayAnimationEnabled
            },
            Tray = new TrayMenuSettings
            {
                SingleClickAction = src.TraySingleClickAction,
                ClickPairing = src.TraySingleClickAction == TrayIconClickAction.OpenWindow
                    ? TrayClickPairing.WindowSingle_QuickTestDouble
                    : TrayClickPairing.QuickTestSingle_WindowDouble
            },
            Ui = new UiAppearanceSettings { Theme = src.UiTheme, Language = src.UiLanguage },
            Meta = new SettingsMeta { SettingsFormatVersion = Math.Max(src.SettingsFormatVersion, 9) },
            SpeedTest = new SpeedTestSettings()
        };
    }
}

/// <summary>Плоский JSON до категорий (миграция).</summary>
internal sealed class AppSettingsFlatLegacy
{
    public bool AutostartEnabled { get; set; }
    public bool MinimizeToTrayOnCloseClick { get; set; }
    public bool MinimizeToTrayOnStartup { get; set; }
    public bool MinimizeToTrayAnimationEnabled { get; set; } = true;
    public TrayIconClickAction? TraySingleClickAction { get; set; }
    public UiTheme UiTheme { get; set; } = UiTheme.Dark;
    public string? UiLanguage { get; set; }
    public int SettingsFormatVersion { get; set; }
}
