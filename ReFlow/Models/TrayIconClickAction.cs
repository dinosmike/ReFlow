namespace ReFlow.Models;

/// <summary>Действие по одиночному левому клику по иконке в области уведомлений.</summary>
public enum TrayIconClickAction
{
    None = 0,
    OpenWindow = 1,

    /// <summary>Устаревшее значение в JSON (раньше — смена режима дисплея).</summary>
    ToggleResolution = 2,

    /// <summary>Устаревшее значение в JSON (раньше — сторонняя функция).</summary>
    ShowRandomAdvice = 3,

    QuickTest = 4
}
