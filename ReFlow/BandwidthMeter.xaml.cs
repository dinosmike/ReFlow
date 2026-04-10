using System;
using System.Windows;

namespace ReFlow;

public partial class BandwidthMeter : System.Windows.Controls.UserControl
{
    /// <summary>9 узлов — 8 равных визуальных сегментов; скорость в Мбит/с вдоль кусочно-линейной шкалы.</summary>
    private static readonly double[] ScaleKnotsMbps = { 0, 5, 10, 50, 100, 250, 500, 750, 1000 };

    public static readonly DependencyProperty SpeedMbpsProperty = DependencyProperty.Register(
        nameof(SpeedMbps),
        typeof(double),
        typeof(BandwidthMeter),
        new PropertyMetadata(0.0, (d, _) => ((BandwidthMeter)d).RefreshVisual()));

    public static readonly DependencyProperty MaxMbpsProperty = DependencyProperty.Register(
        nameof(MaxMbps),
        typeof(double),
        typeof(BandwidthMeter),
        new PropertyMetadata(1000.0, (d, _) => ((BandwidthMeter)d).RefreshVisual()));

    public double SpeedMbps
    {
        get => (double)GetValue(SpeedMbpsProperty);
        set => SetValue(SpeedMbpsProperty, value);
    }

    public double MaxMbps
    {
        get => (double)GetValue(MaxMbpsProperty);
        set => SetValue(MaxMbpsProperty, value);
    }

    public BandwidthMeter()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshVisual();
        SizeChanged += (_, _) => RefreshVisual();
    }

    private void RefreshVisual()
    {
        var ratio = SpeedToVisualFillRatio(SpeedMbps);
        FillScale.ScaleX = ratio <= 0 ? 0 : Math.Max(ratio, 0.004);

        var w = Math.Max(0, TrackInner.ActualWidth);
        MarkerXform.X = w * ratio;
    }

    /// <summary>Доля ширины трека [0..1]: 8 сегментов одинаковой длины, границы по <see cref="ScaleKnotsMbps"/>.</summary>
    private static double SpeedToVisualFillRatio(double mbps)
    {
        var top = ScaleKnotsMbps[^1];
        if (mbps >= top)
            return 1;
        var v = Math.Clamp(mbps, 0, top);
        for (var i = 0; i < 8; i++)
        {
            var a = ScaleKnotsMbps[i];
            var b = ScaleKnotsMbps[i + 1];
            if (v <= b)
            {
                var span = b - a;
                var t = span <= 0 ? 1.0 : (v - a) / span;
                t = Math.Clamp(t, 0, 1);
                return (i + t) / 8.0;
            }
        }

        return 1;
    }
}
