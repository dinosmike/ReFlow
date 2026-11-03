using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using ReFlow.Models;

namespace ReFlow.Services;

/// <summary>Иконка приложения из встроенного <c>app.ico</c> (тот же файл, что <see cref="ApplicationIcon"/> в csproj).</summary>
public static class AppIconFactory
{
    private const string PackUri = "pack://application:,,,/app.ico";

    // Крупные цифры на иконке трея (чёрный фон + текст).
    private const int TrayGlyphSizePx = 64;
    private static readonly object GdiFontInitLock = new();
    private static PrivateFontCollection? _privateUiFonts;
    private static readonly List<GCHandle> _pinnedFontBytes = new();
    private static FontFamily? _segoeUiGdiFamily;

    private static FontFamily SegoeUiGdiFamily
    {
        get
        {
            EnsureGdiSegoeUiFonts();
            return _segoeUiGdiFamily ?? FontFamily.GenericSansSerif;
        }
    }

    private static void EnsureGdiSegoeUiFonts()
    {
        if (_privateUiFonts != null)
            return;
        lock (GdiFontInitLock)
        {
            if (_privateUiFonts != null)
                return;

            var pfc = new PrivateFontCollection();
            // Must match <Resource Include="Fonts\..."> in ReFlow.csproj. Missing names throw from GetResourceStream.
            foreach (var file in new[] { "segoeui.ttf", "segoeuib.ttf", "segoeuil.ttf", "segoeuisl.ttf" })
            {
                System.IO.Stream? stream = null;
                try
                {
                    var info = System.Windows.Application.GetResourceStream(
                        new Uri($"pack://application:,,,/Fonts/{file}", UriKind.Absolute));
                    stream = info?.Stream;
                }
                catch (IOException)
                {
                    continue;
                }

                if (stream == null)
                    continue;

                using (stream)
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    var bytes = ms.ToArray();
                    var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    _pinnedFontBytes.Add(h);
                    pfc.AddMemoryFont(h.AddrOfPinnedObject(), bytes.Length);
                }
            }

            _privateUiFonts = pfc;
            foreach (var ff in pfc.Families)
            {
                if (ff.Name == "Segoe UI")
                {
                    _segoeUiGdiFamily = ff;
                    return;
                }
            }

            if (pfc.Families.Length > 0)
                _segoeUiGdiFamily = pfc.Families[0];
        }
    }

    public static Icon CreateApplicationIcon() => CreateApplicationIcon(UiTheme.Dark);

    /// <param name="theme">Игнорируется — используется один файл <c>app.ico</c>.</param>
    public static Icon CreateApplicationIcon(UiTheme theme) => LoadEmbeddedIconClone();

    public static Icon CreateTrayIcon() => CreateTrayIcon(UiTheme.Dark);

    /// <param name="theme">Игнорируется.</param>
    public static Icon CreateTrayIcon(UiTheme theme) => LoadEmbeddedIconClone();

    public static Icon CreateTraySpinnerIcon(UiTheme theme, int frameIndex)
    {
        using var baseIcon = LoadEmbeddedIconClone();
        using var bmp = baseIcon.ToBitmap();
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var cx = bmp.Width / 2f;
        var cy = bmp.Height / 2f;
        var r = Math.Min(bmp.Width, bmp.Height) * 0.38f;
        var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);

        using var pen = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(2f, bmp.Width / 16f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        var start = (frameIndex % 12) * 30f;
        g.DrawArc(pen, rect, start, 80f);

        return IconFromBitmap(bmp);
    }

    /// <summary>Высота полоски под цифрами: при иконке 16 px это ровно 1 экранный пиксел.</summary>
    private const int TrayBottomBarPx = TrayGlyphSizePx / 16;

    /// <summary>Иконка трея с крупной цифрой: чёрный фон + текст по центру.</summary>
    public static Icon CreateTrayGlyph(string text, Color fore) => CreateTrayGlyph(text, fore, bottomBar: null);

    /// <summary>То же, плюс тонкая полоска по нижней кромке (индикатор «замер идёт»).</summary>
    public static Icon CreateTrayGlyph(string text, Color fore, Color? bottomBar)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            text = "0";
        if (text.Length > 4)
            text = text[..4];

        using var bmp = new Bitmap(TrayGlyphSizePx, TrayGlyphSizePx, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            float em = FindLargestFittingFontEm(g, text, TrayGlyphSizePx);
            using var font = new Font(SegoeUiGdiFamily, em, FontStyle.Bold, GraphicsUnit.Pixel);

            // TextRenderer tends to fit better for tray-sized glyphs.
            System.Windows.Forms.TextRenderer.DrawText(
                g,
                text,
                font,
                new Rectangle(0, 0, TrayGlyphSizePx, TrayGlyphSizePx),
                fore,
                System.Windows.Forms.TextFormatFlags.HorizontalCenter
                | System.Windows.Forms.TextFormatFlags.VerticalCenter
                | System.Windows.Forms.TextFormatFlags.NoPadding
                | System.Windows.Forms.TextFormatFlags.SingleLine);

            if (bottomBar is { } barColor)
            {
                using var bar = new SolidBrush(barColor);
                g.FillRectangle(bar, 0, TrayGlyphSizePx - TrayBottomBarPx, TrayGlyphSizePx, TrayBottomBarPx);
            }
        }

        return IconFromBitmap(bmp);
    }

    /// <summary>
    /// Иконка региона: квадратный флаг страны из встроенного набора (<c>Flags\xx.png</c>, flag-icons 1x1).
    /// Нет флага — две буквы кода; <paramref name="countryCode"/> = null — «?».
    /// <paramref name="dimmed"/> — бледный кадр для мигания во время проверки;
    /// <paramref name="vpnBadge"/> — оранжевая точка в правом нижнем углу: обнаружен VPN.
    /// </summary>
    public static Icon CreateRegionIcon(string? countryCode, bool dimmed = false, bool vpnBadge = false)
    {
        using var bmp = new Bitmap(TrayGlyphSizePx, TrayGlyphSizePx, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var flag = TryLoadFlag(countryCode);
            using var attrs = new ImageAttributes();
            if (dimmed)
                attrs.SetColorMatrix(new ColorMatrix { Matrix33 = 0.3f });

            if (flag != null)
            {
                // Вписываем по ширине с сохранением пропорций, по центру по вертикали.
                var w = TrayGlyphSizePx;
                var h = Math.Min(TrayGlyphSizePx, (int)Math.Round(flag.Height * (double)w / flag.Width));
                var y = (TrayGlyphSizePx - h) / 2;
                g.DrawImage(flag, new Rectangle(0, y, w, h), 0, 0, flag.Width, flag.Height, GraphicsUnit.Pixel, attrs);
            }
            else
            {
                var text = string.IsNullOrWhiteSpace(countryCode) ? "?" : countryCode.Trim().ToUpperInvariant();
                using var lettersBmp = new Bitmap(TrayGlyphSizePx, TrayGlyphSizePx, PixelFormat.Format32bppArgb);
                using (var lg = Graphics.FromImage(lettersBmp))
                {
                    lg.Clear(Color.FromArgb(255, 40, 40, 40));
                    lg.TextRenderingHint = TextRenderingHint.AntiAlias;
                    float em = FindLargestFittingFontEm(lg, text, TrayGlyphSizePx);
                    using var font = new Font(SegoeUiGdiFamily, em, FontStyle.Bold, GraphicsUnit.Pixel);
                    System.Windows.Forms.TextRenderer.DrawText(
                        lg,
                        text,
                        font,
                        new Rectangle(0, 0, TrayGlyphSizePx, TrayGlyphSizePx),
                        Color.White,
                        System.Windows.Forms.TextFormatFlags.HorizontalCenter
                        | System.Windows.Forms.TextFormatFlags.VerticalCenter
                        | System.Windows.Forms.TextFormatFlags.NoPadding
                        | System.Windows.Forms.TextFormatFlags.SingleLine);
                }
                g.DrawImage(lettersBmp, new Rectangle(0, 0, TrayGlyphSizePx, TrayGlyphSizePx),
                    0, 0, TrayGlyphSizePx, TrayGlyphSizePx, GraphicsUnit.Pixel, attrs);
            }

            if (vpnBadge)
            {
                // Треть иконки: при 16 px это ~5 px — различимо, но флаг остаётся узнаваемым.
                const int d = TrayGlyphSizePx * 3 / 8;
                var rect = new Rectangle(TrayGlyphSizePx - d - 1, TrayGlyphSizePx - d - 1, d, d);
                using var fill = new SolidBrush(dimmed ? Color.FromArgb(90, 255, 152, 0) : Color.FromArgb(255, 255, 152, 0));
                using var ring = new Pen(Color.FromArgb(dimmed ? 90 : 255, 20, 20, 20), TrayGlyphSizePx / 16f);
                g.FillEllipse(fill, rect);
                g.DrawEllipse(ring, rect);
            }
        }

        return IconFromBitmap(bmp);
    }

    private static Bitmap? TryLoadFlag(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Trim().Length != 2)
            return null;
        var code = countryCode.Trim().ToLowerInvariant();
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri($"pack://application:,,,/Flags/{code}.png", UriKind.Absolute));
            if (info?.Stream == null)
                return null;
            using (info.Stream)
            using (var src = new Bitmap(info.Stream))
                return new Bitmap(src); // копия: Bitmap держит поток, а поток закрываем
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static float FindLargestFittingFontEm(Graphics g, string text, int boxPx)
    {
        const float MinEm = 5f;
        float hi = boxPx * 0.98f;
        float best = MinEm;

        for (float em = hi; em >= MinEm; em -= 0.35f)
        {
            using var font = new Font(SegoeUiGdiFamily, em, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = System.Windows.Forms.TextRenderer.MeasureText(
                g,
                text,
                font,
                new Size(int.MaxValue, int.MaxValue),
                System.Windows.Forms.TextFormatFlags.NoPadding | System.Windows.Forms.TextFormatFlags.SingleLine);
            if (sz.Width <= boxPx && sz.Height <= boxPx)
            {
                best = em;
                break;
            }
        }

        return best;
    }

    public static Icon CreateTrayIconWithText(UiTheme theme, string text, Color color)
    {
        using var baseIcon = LoadEmbeddedIconClone();
        using var bmp = baseIcon.ToBitmap();
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

        text = (text ?? string.Empty).Trim();
        if (text.Length > 4)
            text = text[..4];

        using var font = new Font("Segoe UI", bmp.Width >= 32 ? 12f : 8f, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);

        var x = (bmp.Width - size.Width) / 2f;
        var y = (bmp.Height - size.Height) / 2f;

        using var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, shadow, x + 1, y + 1);
        g.DrawString(text, font, brush, x, y);

        return IconFromBitmap(bmp);
    }

    private static Icon LoadEmbeddedIconClone()
    {
        var streamInfo = System.Windows.Application.GetResourceStream(new Uri(PackUri, UriKind.Absolute));
        if (streamInfo?.Stream == null)
            throw new InvalidOperationException("Embedded app.ico not found.");

        using (streamInfo.Stream)
        {
            using var icon = new Icon(streamInfo.Stream);
            return (Icon)icon.Clone();
        }
    }

    private static Icon IconFromBitmap(Bitmap bmp)
    {
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Экспорт встроенного <c>app.ico</c> в файл (например для инструментов).</summary>
    public static void SaveApplicationIconFile(string path)
    {
        var streamInfo = System.Windows.Application.GetResourceStream(new Uri(PackUri, UriKind.Absolute));
        if (streamInfo?.Stream == null)
            throw new InvalidOperationException("Embedded app.ico not found.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using (streamInfo.Stream)
        {
            streamInfo.Stream.CopyTo(fs);
        }
    }
}
