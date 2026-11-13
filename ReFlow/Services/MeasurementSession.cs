using ReFlow.Models;
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

    /// <summary>Название сервера, с которого идёт (или шёл последний) замер; null — сервер ещё не выбран.</summary>
    public static string? CurrentServerName { get; private set; }
    public static event Action<SpeedTestProgress>? Progress;
    public static event Action<SpeedTestResult>? Completed;
    /// <summary>Остановлен вручную или упал с ошибкой.</summary>
    public static event Action? Aborted;

    public static void Initialize(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Запустить замер; <c>false</c> — не задан URL файла.</summary>
    public static bool Start(bool restart = false)
    {
        if (restart && _cts != null)
        {
            // Старый прогон отменён; его колбэки отсекает новое поколение, ждать его завершения не нужно.
            Stop();
            _cts = null;
        }
        if (_cts != null)
            return true;

        var s = SettingsStorage.Load();

        var cts = new CancellationTokenSource();
        _cts = cts;
        var gen = ++_generation;
        CurrentServerName = null;
        Started?.Invoke();
        // Выбранный вручную сервер первым, дальше остальные по порядку — если он не отвечает.
        var preferred = s.SpeedTest.PreferredServerUrl;
        var servers = GetServers(s)
            .OrderBy(f => string.Equals(f.Url, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(f => (f.Name, Url: new Uri(f.Url, UriKind.Absolute)))
            // Без автоперебора — только выбранный (или основной): нет связи — ждём его.
            .Take(s.SpeedTest.AutoFallbackServers ? int.MaxValue : 1)
            .ToList();
        _ = RunAsync(gen, cts, servers, TimeSpan.FromSeconds(Math.Clamp(s.SpeedTestDurationSeconds, 1, 600)));
        return true;
    }

    /// <summary>
    /// Все серверы замера по порядку: основной (или свой из настроек), дальше запасные. Названия — на языке программы.
    /// </summary>
    public static List<(string Name, string Url)> GetServers(AppSettings? settings = null)
    {
        var s = settings ?? SettingsStorage.Load();
        var custom = s.SpeedTest.UseCustomDownloadFileUrl ? s.SpeedTest.DownloadFileUrl?.Trim() : null;
        var ru = LocalizationService.CurrentLanguageCode == "ru";
        var main = string.IsNullOrWhiteSpace(custom)
            ? (Name: SpeedTestConfig.DownloadFileName, Url: SpeedTestConfig.DownloadFileUrl)
            : (Name: SpeedTestConfig.CustomFileName, Url: custom);
        return new[] { main }.Concat(SpeedTestConfig.FallbackDownloadFiles)
            .Where(f => Uri.IsWellFormedUriString(f.Url, UriKind.Absolute))
            .DistinctBy(f => f.Url, StringComparer.OrdinalIgnoreCase)
            .Select(f => (Name: ru ? f.Name.Ru : f.Name.En, f.Url))
            .ToList();
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private static async Task RunAsync(int gen, CancellationTokenSource cts, IReadOnlyList<(string Name, Uri Url)> servers, TimeSpan duration)
    {
        var ct = cts.Token;
        var waited = false;
        try
        {
            var dlBase = await ConnectivityProbe.WaitUntilAnyReachableAsync(
                servers.Select(f => f.Url).ToList(),
                () => OnUi(gen, () => { waited = true; WaitingForNetwork?.Invoke(); }),
                ct).ConfigureAwait(false);

            var serverName = servers.First(f => f.Url == dlBase).Name;
            OnUi(gen, () =>
            {
                CurrentServerName = serverName;
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
