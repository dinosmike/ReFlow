using System.Text.Json.Serialization;

namespace ReFlow.Models;

/// <summary>Автозагрузка и поведение окна/трея.</summary>
public sealed class StartupWindowSettings
{
    public bool AutostartEnabled { get; set; }
    public bool MinimizeToTrayOnCloseClick { get; set; }
    public bool MinimizeToTrayOnStartup { get; set; }
    public bool MinimizeToTrayAnimationEnabled { get; set; } = true;

    [JsonPropertyName("askOnExit")]
    public bool AskOnExit { get; set; } = true;
}

/// <summary>Меню иконки в трее.</summary>
public sealed class TrayMenuSettings
{
    /// <summary>Устаревшее поле JSON; при сохранении синхронизируется с <see cref="ClickPairing"/>.</summary>
    public TrayIconClickAction? SingleClickAction { get; set; }

    /// <summary>Как сочетаются одинарный и двойной клик (замер / окно).</summary>
    [JsonPropertyName("clickPairing")]
    public TrayClickPairing ClickPairing { get; set; } = TrayClickPairing.WindowSingle_QuickTestDouble;

    /// <summary>Вторая иконка в трее — флаг страны внешнего IP.</summary>
    [JsonPropertyName("showRegionIcon")]
    public bool ShowRegionIcon { get; set; } = true;

    /// <summary>Проверять регион по таймеру (кроме проверки при смене сети и по клику).</summary>
    [JsonPropertyName("regionAutoCheck")]
    public bool RegionAutoCheck { get; set; }

    /// <summary>Интервал периодической проверки региона, минуты (1–1440).</summary>
    [JsonPropertyName("regionCheckIntervalMinutes")]
    public int RegionCheckIntervalMinutes { get; set; } = 5;

    /// <summary>Усиленный режим: искать реальную страну запросом в обход VPN.</summary>
    [JsonPropertyName("regionEnhanced")]
    public bool RegionEnhanced { get; set; }
}

/// <summary>Тема и язык интерфейса.</summary>
public sealed class UiAppearanceSettings
{
    public UiTheme Theme { get; set; } = UiTheme.Dark;
    public string? Language { get; set; }
}

/// <summary>Служебные поля файла настроек.</summary>
public sealed class SettingsMeta
{
    /// <summary>9 — tray clickPairing (одинарный/двойной клик).</summary>
    public int SettingsFormatVersion { get; set; }
}
