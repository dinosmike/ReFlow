using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ReFlow.Services;

namespace ReFlow;

public partial class AutostartPromptWindow
{
    private bool _answered;

    public AutostartPromptWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Yes_OnClick(object sender, RoutedEventArgs e)
    {
        _answered = true;
        var s = SettingsStorage.Load();
        s.AutostartEnabled = true;
        s.AskOnExit = false;
        SettingsStorage.Save(s);
        AutostartService.SetEnabled(true);
        Close();
    }

    private void No_OnClick(object sender, RoutedEventArgs e)
    {
        _answered = true;
        var s = SettingsStorage.Load();
        s.AskOnExit = false;
        SettingsStorage.Save(s);
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_answered)
        {
            var s = SettingsStorage.Load();
            s.AskOnExit = false;
            SettingsStorage.Save(s);
        }
        base.OnClosing(e);
    }
}
