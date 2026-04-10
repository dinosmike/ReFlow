namespace ReFlow.Models;

/// <summary>Распределение «быстрый замер» и «окно» между одинарным и двойным кликом по иконке в трее.</summary>
public enum TrayClickPairing
{
    /// <summary>Один клик — быстрый замер, двойной — окно.</summary>
    QuickTestSingle_WindowDouble = 0,

    /// <summary>Один клик — окно, двойной — быстрый замер.</summary>
    WindowSingle_QuickTestDouble = 1
}
