using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace ReFlow.Services;

/// <summary>
/// Лёгкая проверка «сервер замера доступен». Полный спидтест не запускается, пока пробник не ответит.
/// </summary>
public static class ConnectivityProbe
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    /// <summary>Скрытый режим для просмотра UI ожидания: пробник всегда «нет сети».</summary>
    internal static bool SimulateOffline { get; set; }

    /// <summary>Мгновенная оценка без запросов: Windows не видит ни одного подключённого адаптера. «false» ещё не значит, что интернет есть.</summary>
    public static bool IsNetworkLikelyDown => SimulateOffline || !NetworkInterface.GetIsNetworkAvailable();

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    /// <summary>Ждёт, пока GET (только заголовки) к файлу замера не завершится успешно. «Стоп» отменяет через <paramref name="cancellationToken"/>.</summary>
    public static async Task WaitUntilReachableAsync(Uri downloadUrl, Action? onWaiting, CancellationToken cancellationToken)
    {
        if (downloadUrl == null) throw new ArgumentNullException(nameof(downloadUrl));

        var announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsReachableAsync(downloadUrl, cancellationToken).ConfigureAwait(false))
                return;

            if (!announced)
            {
                announced = true;
                onWaiting?.Invoke();
            }

            await WaitForRetryOrNetworkAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsReachableAsync(Uri downloadUrl, CancellationToken cancellationToken)
    {
        if (SimulateOffline)
            return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, WithCacheBuster(downloadUrl));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Пауза между пробниками или сразу, если Windows сообщила, что сеть изменилась (модем поднялся).</summary>
    private static async Task WaitForRetryOrNetworkAsync(CancellationToken cancellationToken)
    {
        if (SimulateOffline)
        {
            await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
            return;
        }
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnAvailability(object? sender, NetworkAvailabilityEventArgs e) => wake.TrySetResult();
        void OnAddress(object? sender, EventArgs e) => wake.TrySetResult();

        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
        NetworkChange.NetworkAddressChanged += OnAddress;
        try
        {
            using var reg = cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), wake);
            var delay = Task.Delay(RetryInterval, cancellationToken);
            var completed = await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || completed.IsCanceled)
                cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
            NetworkChange.NetworkAddressChanged -= OnAddress;
        }
    }

    private static Uri WithCacheBuster(Uri url)
    {
        var b = new UriBuilder(url);
        var q = b.Query.StartsWith('?') ? b.Query[1..] : b.Query;
        var extra = "r=" + Guid.NewGuid().ToString("N");
        b.Query = string.IsNullOrEmpty(q) ? extra : q + "&" + extra;
        return b.Uri;
    }
}
