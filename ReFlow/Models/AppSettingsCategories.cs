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
    public TrayIconClickAction? SingleClickAction { get; set; }
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
    /// <summary>7 — ReFlow без профилей дисплея и советов.</summary>
    public int SettingsFormatVersion { get; set; }
}
