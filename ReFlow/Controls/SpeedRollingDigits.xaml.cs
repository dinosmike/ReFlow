using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ReFlow.Controls;

/// <summary>
/// Три цифры 0–999 с вертикальным «проскальзыванием», по мотивам TimerDigitGroup из ReQuit.
/// </summary>
public partial class SpeedRollingDigits : System.Windows.Controls.UserControl
{
    private int _value;
    private bool _isAnimating;
    private int _animationsRemaining;

    private const double TickAnimMs = 300;
    private const double DefaultCellHeight = 130;
    private const double DigitBorderCornerRadius = 20;

    private TextBlock[] _cur = null!;
    private TextBlock[] _inc = null!;
    private TranslateTransform[] _curTf = null!;
    private TranslateTransform[] _incTf = null!;

    private static readonly SolidColorBrush ResultDigitForeground = CreateFrozenBrush("#44b759");

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public SpeedRollingDigits()
    {
        InitializeComponent();
        DigitBorder.SizeChanged += (_, _) => UpdateDigitBorderClip();
        Loaded += (_, _) => UpdateDigitBorderClip();

        _cur = new[] { CurH, CurT, CurO };
        _inc = new[] { IncH, IncT, IncO };
        _curTf = new[] { CurHTransform, CurTTransform, CurOTransform };
        _incTf = new[] { IncHTransform, IncTTransform, IncOTransform };
    }

    /// <summary>После успешного замера — цифры #44b759; иначе цвет темы.</summary>
    public void SetResultHighlight(bool enabled)
    {
        for (var i = 0; i < 3; i++)
        {
            if (enabled)
            {
                _cur[i].Foreground = ResultDigitForeground;
                _inc[i].Foreground = ResultDigitForeground;
            }
            else
            {
                _cur[i].SetResourceReference(TextBlock.ForegroundProperty, "Rf.Text.Primary");
                _inc[i].SetResourceReference(TextBlock.ForegroundProperty, "Rf.Text.Primary");
            }
        }
    }

    private void UpdateDigitBorderClip()
    {
        if (DigitBorder.ActualWidth <= 0 || DigitBorder.ActualHeight <= 0)
            return;
        DigitBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, DigitBorder.ActualWidth, DigitBorder.ActualHeight),
            DigitBorderCornerRadius,
            DigitBorderCornerRadius);
    }

    public int Value => _value;

    /// <summary>Анимированная смена значения. Направление как в ReQuit: slideDown при уменьшении, иначе снизу.</summary>
    public void AnimateToValue(int newValue, bool slideDown = true)
    {
        newValue = Math.Clamp(newValue, 0, 999);
        var prev = _value;
        if (newValue == prev && !_isAnimating)
            return;

        if (_isAnimating)
            SnapFinish();

        _value = newValue;
        RunMultiColumnAnimation(prev, newValue, slideDown, TickAnimMs);
    }

    public void SetValueSilent(int newValue)
    {
        if (_isAnimating)
            SnapFinish();

        _value = Math.Clamp(newValue, 0, 999);
        ApplyDigitDisplay(_value);
    }

    private static void Decompose(int v, out int h, out int t, out int ones)
    {
        h = v / 100;
        t = v / 10 % 10;
        ones = v % 10;
    }

    private void ApplyDigitDisplay(int v)
    {
        Decompose(v, out var h, out var t, out var ones);
        for (var i = 0; i < 3; i++)
        {
            var d = i switch { 0 => h, 1 => t, _ => ones };
            _cur[i].Text = d.ToString();
            _inc[i].Visibility = Visibility.Collapsed;
            _curTf[i].Y = 0;
            _incTf[i].Y = 0;
        }
    }

    private void RunMultiColumnAnimation(int from, int to, bool slideDown, double durationMs)
    {
        Decompose(from, out var fh, out var ft, out var fo);
        Decompose(to, out var th, out var tt, out var toOnes);

        var changeH = fh != th;
        var changeT = ft != tt;
        var changeO = fo != toOnes;
        if (!changeH && !changeT && !changeO)
        {
            ApplyDigitDisplay(to);
            return;
        }

        _isAnimating = true;
        _animationsRemaining = (changeH ? 1 : 0) + (changeT ? 1 : 0) + (changeO ? 1 : 0);

        if (changeH) StartColumnAnim(0, fh, th, slideDown, durationMs);
        else _cur[0].Text = th.ToString();

        if (changeT) StartColumnAnim(1, ft, tt, slideDown, durationMs);
        else _cur[1].Text = tt.ToString();

        if (changeO) StartColumnAnim(2, fo, toOnes, slideDown, durationMs);
        else _cur[2].Text = toOnes.ToString();
    }

    private void StartColumnAnim(int col, int fromD, int toD, bool slideDown, double durationMs)
    {
        var curT = _cur[col];
        var incT = _inc[col];
        var curX = _curTf[col];
        var incX = _incTf[col];

        var h = DigitBorder.ActualHeight > 0 ? DigitBorder.ActualHeight : DefaultCellHeight;
        var inStart = slideDown ? -h : h;
        var curEnd = slideDown ? h : -h;

        curT.Text = fromD.ToString();
        incT.Text = toD.ToString();
        incT.Visibility = Visibility.Visible;

        incX.Y = inStart;
        curX.Y = 0;

        var dur = TimeSpan.FromMilliseconds(durationMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animCur = new DoubleAnimation(0, curEnd, dur) { EasingFunction = ease };
        var animInc = new DoubleAnimation(inStart, 0, dur) { EasingFunction = ease };

        animInc.Completed += (_, _) => OnColumnAnimComplete();

        curX.BeginAnimation(TranslateTransform.YProperty, animCur);
        incX.BeginAnimation(TranslateTransform.YProperty, animInc);
    }

    private void OnColumnAnimComplete()
    {
        if (System.Threading.Interlocked.Decrement(ref _animationsRemaining) > 0)
            return;

        for (var i = 0; i < 3; i++)
        {
            _curTf[i].BeginAnimation(TranslateTransform.YProperty, null);
            _incTf[i].BeginAnimation(TranslateTransform.YProperty, null);
        }

        ApplyDigitDisplay(_value);
        _isAnimating = false;
    }

    private void SnapFinish()
    {
        for (var i = 0; i < 3; i++)
        {
            _curTf[i].BeginAnimation(TranslateTransform.YProperty, null);
            _incTf[i].BeginAnimation(TranslateTransform.YProperty, null);
            if (_inc[i].Visibility == Visibility.Visible)
                _cur[i].Text = _inc[i].Text;
            _curTf[i].Y = 0;
            _incTf[i].Y = 0;
            _inc[i].Visibility = Visibility.Collapsed;
        }

        _isAnimating = false;
        _animationsRemaining = 0;
    }
}
