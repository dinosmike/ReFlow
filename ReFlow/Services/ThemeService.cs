using System.Windows;
using ReFlow.Models;

namespace ReFlow.Services;

public static class ThemeService
{
    public static void Apply(UiTheme theme)
    {
        var uri = GetThemeUri(theme);
        var rd = new ResourceDictionary { Source = uri };
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = rd;
        else
            merged.Add(rd);

        if (System.Windows.Application.Current is global::ReFlow.App app)
            app.RefreshIconsForTheme(theme);
    }

    private static Uri GetThemeUri(UiTheme _) =>
        new Uri("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);
}
