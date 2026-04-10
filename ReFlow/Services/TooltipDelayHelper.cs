using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReFlow.Services;

/// <summary>
/// Задаёт <see cref="ToolTipService.InitialShowDelay"/> на каждом <see cref="FrameworkElement"/> в поддереве.
/// Наследование от корня окна в WPF на практике часто не срабатывает для подсказок на кнопках с ControlTemplate.
/// </summary>
public static class TooltipDelayHelper
{
    public static void Apply(DependencyObject root, int delayMs)
    {
        if (delayMs < 0)
            delayMs = 0;

        ApplyRecursive(root, delayMs);
    }

    private static void ApplyRecursive(DependencyObject d, int delayMs)
    {
        if (d is FrameworkElement fe)
            ToolTipService.SetInitialShowDelay(fe, delayMs);

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            ApplyRecursive(VisualTreeHelper.GetChild(d, i), delayMs);
    }
}
