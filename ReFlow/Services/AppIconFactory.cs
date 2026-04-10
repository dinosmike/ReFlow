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

    /// <summary>Иконка трея с крупной цифрой: чёрный фон + текст по центру.</summary>
    public static Icon CreateTrayGlyph(string text, Color fore)
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
        }

        return IconFromBitmap(bmp);
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
