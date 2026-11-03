using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;

namespace ReFlow.Services;

/// <summary>
/// Определяет страну внешнего IP (то, как нас видит интернет, в том числе через VPN),
/// а в усиленном режиме — ещё и реальную страну запросом в обход VPN (<see cref="VpnBypassProbe"/>).
/// Создаётся и используется в UI-потоке; события приходят в UI-потоке.
/// </summary>
public sealed class RegionService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    /// <summary>После смены сети адрес устаканивается не сразу (VPN поднимается, DHCP).</summary>
    private static readonly TimeSpan NetworkChangeDebounce = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http = new() { Timeout = RequestTimeout };
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _periodicTimer;
    private readonly DispatcherTimer _networkDebounceTimer;
    private bool _checking;
    private bool _enabled;
    private bool _enhanced;

    public RegionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _periodicTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _periodicTimer.Tick += (_, _) => _ = CheckNowAsync();
        _networkDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = NetworkChangeDebounce };
        _networkDebounceTimer.Tick += (_, _) =>
        {
            _networkDebounceTimer.Stop();
            _ = CheckNowAsync();
        };
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>Страна, которую показываем: реальная, если усиленный режим нашёл VPN, иначе страна внешнего IP.</summary>
    public string? CountryCode => VpnDetected && RealCountryCode != null ? RealCountryCode : IpCountryCode;

    /// <summary>Страна внешнего IP (под VPN — страна VPN-сервера). Null — ещё ни разу не определили.</summary>
    public string? IpCountryCode { get; private set; }

    /// <summary>Усиленный режим: страна запроса в обход VPN. Null — режим выключен или обход не удался.</summary>
    public string? RealCountryCode { get; private set; }

    /// <summary>Усиленный режим: адрес в обход VPN отличается от обычного — трафик идёт через VPN/прокси.</summary>
    public bool VpnDetected { get; private set; }

    /// <summary>Усиленный режим включён, но запрос в обход VPN не прошёл (kill switch, нет физического адаптера).</summary>
    public bool RealCheckFailed { get; private set; }

    /// <summary>Последняя проверка не удалась (сети нет / сервисы не ответили); прежние значения сохранены.</summary>
    public bool LastCheckFailed { get; private set; }

    public bool IsChecking => _checking;

    public event Action? CheckStarted;
    public event Action? CheckCompleted;

    /// <summary>
    /// <paramref name="enabled"/> — иконка показана (иначе никаких запросов);
    /// <paramref name="periodic"/> + <paramref name="intervalMinutes"/> — проверка по таймеру;
    /// <paramref name="enhanced"/> — дополнительно искать реальную страну в обход VPN.
    /// </summary>
    public void Configure(bool enabled, bool periodic, int intervalMinutes, bool enhanced)
    {
        _enabled = enabled;
        var enhancedChanged = _enhanced != enhanced;
        _enhanced = enhanced;
        _periodicTimer.Stop();
        if (!enabled)
        {
            _networkDebounceTimer.Stop();
            return;
        }

        if (periodic)
        {
            _periodicTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 1, 1440));
            _periodicTimer.Start();
        }

        // Режим переключили — результат прежней проверки уже не про то; перепроверяем.
        if (enhancedChanged && IpCountryCode != null)
            _ = CheckNowAsync();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (!_enabled)
                return;
            _networkDebounceTimer.Stop();
            _networkDebounceTimer.Start();
        });
    }

    public async Task CheckNowAsync()
    {
        if (_checking || !_enabled)
            return;
        _checking = true;
        CheckStarted?.Invoke();
        try
        {
            var direct = await LookupAsync(_http);
            LastCheckFailed = direct == null;
            if (direct == null)
                return;

            IpCountryCode = direct.Country;
            if (!_enhanced)
            {
                RealCountryCode = null;
                VpnDetected = false;
                RealCheckFailed = false;
                return;
            }

            var bypass = await LookupBypassingVpnAsync();
            RealCheckFailed = bypass == null;
            RealCountryCode = bypass?.Country;
            // Сравниваем адреса, а не страны: VPN может быть и в своей же стране.
            VpnDetected = bypass != null && !string.Equals(bypass.Ip, direct.Ip, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _checking = false;
            // Таймер считаем от последней проверки, чтобы ручная/сетевая не дублировалась плановой.
            if (_periodicTimer.IsEnabled)
            {
                _periodicTimer.Stop();
                _periodicTimer.Start();
            }
            CheckCompleted?.Invoke();
        }
    }

    private sealed record LookupResult(string? Ip, string Country);

    private static async Task<LookupResult?> LookupAsync(HttpClient http)
    {
        if (ConnectivityProbe.SimulateOffline)
        {
            await Task.Delay(1500);
            return null;
        }

        // Два независимых бесплатных сервиса без ключа: второй — запасной.
        return await TryCountryIsAsync(http) ?? await TryIpInfoAsync(http);
    }

    private static async Task<LookupResult?> LookupBypassingVpnAsync()
    {
        foreach (var local in VpnBypassProbe.GetPhysicalIPv4Addresses())
        {
            using var http = VpnBypassProbe.CreateBoundClient(local);
            var result = await LookupAsync(http);
            if (result != null)
                return result;
        }
        return null;
    }

    /// <summary>https://country.is — открытый сервис, ответ <c>{"ip":"…","country":"RU"}</c>.</summary>
    private static Task<LookupResult?> TryCountryIsAsync(HttpClient http) =>
        TryJsonAsync(http, "https://api.country.is/");

    /// <summary>https://ipinfo.io/json — запасной, ответ <c>{"ip":"…","country":"RU",…}</c>.</summary>
    private static Task<LookupResult?> TryIpInfoAsync(HttpClient http) =>
        TryJsonAsync(http, "https://ipinfo.io/json");

    private static async Task<LookupResult?> TryJsonAsync(HttpClient http, string url)
    {
        try
        {
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var country = root.TryGetProperty("country", out var c) ? NormalizeCode(c.GetString()) : null;
            if (country == null)
                return null;
            var ip = root.TryGetProperty("ip", out var i) ? i.GetString() : null;
            return new LookupResult(ip, country);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or SocketException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? NormalizeCode(string? raw)
    {
        var code = raw?.Trim().ToUpperInvariant();
        return code is { Length: 2 } && char.IsLetter(code[0]) && char.IsLetter(code[1]) ? code : null;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _periodicTimer.Stop();
        _networkDebounceTimer.Stop();
        _http.Dispose();
    }
}
