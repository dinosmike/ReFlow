using System.IO;
using System.Media;

namespace ReFlow.Services;

/// <summary>Радостная мелодия «сеть вернулась» (~2,5 с), синтезируется в памяти.</summary>
public static class ReconnectChime
{
    private static byte[]? _wav;
    private static SoundPlayer? _player;

    public static void Play()
    {
        try
        {
            _wav ??= BuildWav();
            _player ??= new SoundPlayer(new MemoryStream(_wav));
            _player.Stream!.Position = 0;
            _player.Play();
        }
        catch
        {
            // Нет аудиоустройства — молча пропускаем.
        }
    }

    private static byte[] BuildWav()
    {
        const int rate = 44100;
        // «Тун-туру-дум-тууум»: до-ми-соль, высокое ми и долгое до (C6 E6 G6 E7 C7), последнее звенит.
        (double freq, double start, double length, double decay)[] notes =
        {
            (1046.50, 0.00, 0.6, 5.5),
            (1318.51, 0.20, 0.6, 5.5),
            (1567.98, 0.40, 0.6, 5.5),
            (2637.02, 0.60, 0.6, 5.5),
            (2093.00, 1.00, 1.6, 2.0),
        };
        var total = (int)(rate * 2.6);
        var mix = new double[total];
        foreach (var (freq, start, length, decay) in notes)
        {
            var from = (int)(start * rate);
            var count = (int)(length * rate);
            for (var i = 0; i < count && from + i < total; i++)
            {
                var t = i / (double)rate;
                var attack = Math.Min(1.0, t / 0.008);
                var env = attack * Math.Exp(-t * decay);
                // Основной тон и тихая октава сверху — звучит как колокольчик, а не писк.
                mix[from + i] += env * (Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(4 * Math.PI * freq * t));
            }
        }

        var peak = mix.Max(Math.Abs);
        var scale = peak > 0 ? 0.35 * short.MaxValue / peak : 0;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + total * 2); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(total * 2);
        foreach (var v in mix)
            w.Write((short)Math.Round(v * scale));
        w.Flush();
        return ms.ToArray();
    }
}
