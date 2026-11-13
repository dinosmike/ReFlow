using System.Net.Http;
using System.Windows.Threading;

namespace ReFlow.Services;

/// <summary>
/// Один замер на всё приложение: окно и трей запускают/останавливают его и рисуют каждый своё
/// (циферблат и цифры на иконке) по одним и тем же событиям. События приходят в UI-поток.
/// </summary>
public static class MeasurementSession
{
    private static readonly SpeedTestService SpeedTest = new(new HttpClient());
    private static Dispatcher? _dispatcher;
    private static CancellationTokenSource? _cts;
    private static int _generation;

    public static bool IsRunning => _cts != null;

    /// <summary>Нажали «Старт» — до первого пробника сети.</summary>
    public static event Action? Started;
    /// <summary>Сервер замера недоступен, ждём сеть.</summary>
    public static event Action? WaitingForNetwork;
    /// <summary>Сеть есть, качаем; аргумент — длительность замера.</summary>
    public static event Action<TimeSpan>? Measuring;
    public static event Action<SpeedTestProgress>? Progress;
    public static event Action<SpeedTestResult>? Completed;
    /// <summary>Остановлен вручную или упал с ошибкой.</summary>
    public static event Action? Aborted;

    public static void Initialize(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Запустить замер; <c>false</c> — не задан URL файла.</summary>
    public static bool Start(bool restart = false)
    {
        if (restart)
            Stop();
        if (_cts != null)
            return true;

        var s = SettingsStorage.Load();
        var fileUrlRaw = ((s.SpeedTest.UseCustomDownloadFileUrl ? s.SpeedTest.DownloadFileUrl : null) ?? SpeedTestConfig.DownloadFileUrl).Trim();
        if (string.IsNullOrWhiteSpace(fileUrlRaw))
            return false;

        var cts = new CancellationTokenSource();
        _cts = cts;
        var gen = ++_generation;
        Started?.Invoke();
        _ = RunAsync(gen, cts, new Uri(fileUrlRaw, UriKind.Absolute), TimeSpan.FromSeconds(Math.Clamp(s.SpeedTestDurationSeconds, 1, 600)));
        return true;
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private static async Task RunAsync(int gen, CancellationTokenSource cts, Uri dlBase, TimeSpan duration)
    {
        var ct = cts.Token;
        var waited = false;
        try
        {
            await ConnectivityProbe.WaitUntilReachableAsync(
                dlBase,
                () => OnUi(gen, () => { waited = true; WaitingForNetwork?.Invoke(); }),
                ct).ConfigureAwait(false);

            OnUi(gen, () =>
            {
                if (waited && SettingsStorage.Load().PlaySoundOnReconnect)
                    ReconnectChime.Play();
                Measuring?.Invoke(duration);
            });

            var options = new SpeedTestOptions
            {
                Duration = duration,
                ParallelStreams = 4,
                ProgressInterval = TimeSpan.FromMilliseconds(50),
                DownloadUrlBuilder = () => new Uri(dlBase + (dlBase.Query.Length > 0 ? "&" : "?") + "r=" + Guid.NewGuid().ToString("N"))
            };
            var prog = new Progress<SpeedTestProgress>(p => OnUi(gen, () => Progress?.Invoke(p)));
            var result = await SpeedTest.RunAsync(options, prog, ct).ConfigureAwait(false);
            OnUi(gen, () => { Finish(gen, cts); Completed?.Invoke(result); });
        }
        catch
        {
            OnUi(gen, () => { Finish(gen, cts); Aborted?.Invoke(); });
        }
    }

    private static void Finish(int gen, CancellationTokenSource cts)
    {
        if (gen != _generation || !ReferenceEquals(_cts, cts))
            return;
        _cts = null;
        try { cts.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Колбэки отменённого прогона (после перезапуска) не трогают UI нового.</summary>
    private static void OnUi(int gen, Action action)
    {
        void Run()
        {
            if (gen == _generation)
                action();
        }

        var d = _dispatcher;
        if (d == null || d.CheckAccess())
            Run();
        else
            d.BeginInvoke(Run);
    }
}
