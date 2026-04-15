using System;
using System.Windows;
using System.Windows.Media;

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
        Loaded += (_, _) =>
        {
            UpdateTrackClip();
            RefreshVisual();
        };
        SizeChanged += (_, _) =>
        {
            UpdateTrackClip();
            RefreshVisual();
        };
    }

    /// <summary>Совпадает с вычитанием в формуле центра маркера — заливка тянется ровно до центра кружка.</summary>
    private const double MarkerTrackEndOffset = 3;

    /// <summary>Доводка вправо: визуальный край градиента чуть впереди математического (обводка/сглаживание).</summary>
    private const double FillExtendPastMarkerPx = 3;

    private double GetTrackRadius()
    {
        if (TrackInnerBorder == null)
            return 0;
        var h = TrackInnerBorder.ActualHeight;
        return h > 0 ? h / 2.0 : 0;
    }

    private void UpdateTrackClip()
    {
        // Border.CornerRadius + ClipToBounds не обрезает дочерние элементы по скруглению.
        // Явный Clip гарантирует, что заливка не выйдет за форму "пилюли".
        if (TrackInnerBorder == null)
            return;
        var w = TrackInnerBorder.ActualWidth;
        var h = TrackInnerBorder.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            TrackInnerBorder.Clip = null;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(TrackInnerBorder);
        static double Snap(double v, double scale) => Math.Round(v * scale) / scale;

        w = Snap(w, dpi.DpiScaleX);
        h = Snap(h, dpi.DpiScaleY);
        var r = Snap(h / 2.0, dpi.DpiScaleY);

        TrackInnerBorder.CornerRadius = new CornerRadius(r);
        if (TrackOutline != null)
            TrackOutline.CornerRadius = new CornerRadius(r);

        // Идеальная "пилюля": прямоугольник + два круга (Union).
        // На некоторых DPI RectangleGeometry даёт визуально "кривые" углы из-за антиалиасинга.
        if (w <= r * 2)
        {
            // Деградация: круг.
            TrackInnerBorder.Clip = new EllipseGeometry(new Rect(0, 0, w, h));
            return;
        }

        var left = new EllipseGeometry(new System.Windows.Point(r, r), r, r);
        var right = new EllipseGeometry(new System.Windows.Point(w - r, r), r, r);
        var mid = new RectangleGeometry(new Rect(r, 0, w - 2 * r, h));
        var pill = new CombinedGeometry(
            GeometryCombineMode.Union,
            new CombinedGeometry(GeometryCombineMode.Union, left, mid),
            right);
        pill.Freeze();
        TrackInnerBorder.Clip = pill;
    }

    private void RefreshVisual()
    {
        var ratio = SpeedToVisualFillRatio(SpeedMbps);
        var w = Math.Max(0, TrackInner.ActualWidth);
        if (w <= 0)
        {
            if (FillPath != null)
                FillPath.Data = null;
            MarkerXform.X = 0;
            return;
        }

        var span = Math.Max(0, w - 10);
        var markerCenterX = 10 + span * ratio - MarkerTrackEndOffset;
        MarkerXform.X = Math.Round(markerCenterX);

        if (FillPath == null)
            return;

        var h = TrackInnerBorder.ActualHeight;
        if (h <= 0)
        {
            FillPath.Data = null;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(TrackInnerBorder);
        static double Snap(double v, double scale) => Math.Round(v * scale) / scale;

        h = Snap(h, dpi.DpiScaleY);
        w = Snap(w, dpi.DpiScaleX);
        var r = Snap(h / 2.0, dpi.DpiScaleY);

        if (ratio <= 0)
        {
            FillPath.Data = null;
            return;
        }

        var fillEndX = markerCenterX + FillExtendPastMarkerPx;
        var fillW = Snap(Math.Clamp(fillEndX, 0.0, w), dpi.DpiScaleX);
        // Минимум 2R, чтобы оба полукруга были корректными при очень малой ширине.
        fillW = Math.Max(fillW, Snap(r * 2, dpi.DpiScaleX));
        fillW = Math.Min(fillW, w);

        FillPath.Data = BuildCapsuleGeometry(fillW, h, r);
    }

    private static Geometry BuildCapsuleGeometry(double w, double h, double r)
    {
        if (w <= 0 || h <= 0)
            return Geometry.Empty;

        var rr = Math.Min(r, w / 2.0);
        rr = Math.Min(rr, h / 2.0);

        // Строим капсулу с одинаковыми дугами сверху/снизу: никаких артефактов RectangleGeometry.
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var x0 = 0.0;
            var x1 = w;
            var y0 = 0.0;
            var y1 = h;

            ctx.BeginFigure(new System.Windows.Point(x0 + rr, y0), isFilled: true, isClosed: true);
            ctx.LineTo(new System.Windows.Point(x1 - rr, y0), isStroked: true, isSmoothJoin: false);
            ctx.ArcTo(new System.Windows.Point(x1 - rr, y1), new System.Windows.Size(rr, rr), 0, false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new System.Windows.Point(x0 + rr, y1), isStroked: true, isSmoothJoin: false);
            ctx.ArcTo(new System.Windows.Point(x0 + rr, y0), new System.Windows.Size(rr, rr), 0, false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        g.Freeze();
        return g;
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
