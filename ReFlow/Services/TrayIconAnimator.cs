using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;

namespace ReFlow.Services;

/// <summary>
/// Держит картинку одной <see cref="NotifyIcon"/>: ровную или мигающую из двух кадров.
/// Все переданные сюда иконки становятся собственностью аниматора и освобождаются им.
/// </summary>
public sealed class TrayIconAnimator : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _timer;
    private Icon? _static;
    private Icon? _on;
    private Icon? _off;
    private string? _blinkKey;
    private bool _onPhase;

    public TrayIconAnimator(NotifyIcon notifyIcon, Dispatcher dispatcher, TimeSpan interval)
    {
        _notifyIcon = notifyIcon;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = interval };
        _timer.Tick += (_, _) =>
        {
            if (_on == null || _off == null)
                return;
            _onPhase = !_onPhase;
            _notifyIcon.Icon = _onPhase ? _on : _off;
        };
    }

    public bool IsBlinking => _timer.IsEnabled;

    /// <summary>Ровная картинка; мигание останавливается.</summary>
    public void Show(Icon icon)
    {
        _timer.Stop();
        var oldStatic = _static;
        var oldOn = _on;
        var oldOff = _off;
        _static = icon;
        _on = _off = null;
        _blinkKey = null;
        _notifyIcon.Icon = icon;
        DisposeUnlessSame(oldStatic, icon);
        oldOn?.Dispose();
        oldOff?.Dispose();
    }

    /// <summary>
    /// Мигать двумя кадрами. Тот же <paramref name="key"/> при уже идущем мигании — ничего не делает;
    /// новый ключ подменяет кадры, не сбивая ритм.
    /// </summary>
    public void Blink(string key, Func<Icon> createOn, Func<Icon> createOff)
    {
        if (_timer.IsEnabled && key == _blinkKey)
            return;

        var oldStatic = _static;
        var oldOn = _on;
        var oldOff = _off;
        _on = createOn();
        _off = createOff();
        _static = null;
        _blinkKey = key;

        if (!_timer.IsEnabled)
        {
            _onPhase = true;
            _timer.Start();
        }
        _notifyIcon.Icon = _onPhase ? _on : _off;

        oldStatic?.Dispose();
        oldOn?.Dispose();
        oldOff?.Dispose();
    }

    private static void DisposeUnlessSame(Icon? old, Icon current)
    {
        if (old != null && !ReferenceEquals(old, current))
            old.Dispose();
    }

    /// <summary>Вызывать после того, как NotifyIcon скрыт/освобождён.</summary>
    public void Dispose()
    {
        _timer.Stop();
        _static?.Dispose();
        _on?.Dispose();
        _off?.Dispose();
        _static = _on = _off = null;
    }
}
