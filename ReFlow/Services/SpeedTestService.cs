using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ReFlow.Services;

public sealed class SpeedTestService
{
    private readonly HttpClient _http;

    public SpeedTestService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<SpeedTestResult> RunAsync(SpeedTestOptions options, IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        options.Validate();

        var download = await RunDownloadAsync(options, progress, cancellationToken).ConfigureAwait(false);
        return new SpeedTestResult(download);
    }

    private async Task<SpeedTestMetric> RunDownloadAsync(SpeedTestOptions options, IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report(SpeedTestProgress.PhaseChanged(SpeedTestPhase.Download));

        long totalBytes = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.Duration);

        var tasks = new List<Task>(capacity: options.ParallelStreams);
        for (var i = 0; i < options.ParallelStreams; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var buffer = new byte[options.BufferSizeBytes];

                while (!cts.IsCancellationRequested)
                {
                    var url = options.DownloadUrlBuilder!();
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    while (!cts.IsCancellationRequested)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
                        if (n <= 0) break;
                        Interlocked.Add(ref totalBytes, n);
                    }
                }
            }, cts.Token));
        }

        // Soft progress loop (UI updates). CancelAfter(duration) cancels cts.Token; Delay then throws OCE.
        // We must not let that escape — otherwise we never reach the final average / Completed report (tray stays red).
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.ProgressInterval, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            progress?.Report(SpeedTestProgress.Rate(SpeedTestPhase.Download, BytesToMbps(Interlocked.Read(ref totalBytes), sw.Elapsed)));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* expected on cancellation */ }

        var elapsed = sw.Elapsed;
        var avgMbps = BytesToMbps(Interlocked.Read(ref totalBytes), elapsed);
        progress?.Report(SpeedTestProgress.Completed(SpeedTestPhase.Download, avgMbps));
        return new SpeedTestMetric(avgMbps, elapsed);
    }


    private static double BytesToMbps(long totalBytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0) return 0;
        var bits = totalBytes * 8.0;
        var mbps = bits / 1_000_000.0 / elapsed.TotalSeconds;
        return mbps < 0 ? 0 : mbps;
    }
}

public sealed record SpeedTestResult(SpeedTestMetric Download);

public sealed record SpeedTestMetric(double AverageMbps, TimeSpan Elapsed);

public enum SpeedTestPhase
{
    Download
}

public sealed record SpeedTestProgress(SpeedTestPhase Phase, SpeedTestProgressKind Kind, double? Mbps)
{
    public static SpeedTestProgress PhaseChanged(SpeedTestPhase phase) => new(phase, SpeedTestProgressKind.PhaseChanged, null);
    public static SpeedTestProgress Rate(SpeedTestPhase phase, double mbps) => new(phase, SpeedTestProgressKind.Rate, mbps);
    public static SpeedTestProgress Completed(SpeedTestPhase phase, double avgMbps) => new(phase, SpeedTestProgressKind.Completed, avgMbps);
}

public enum SpeedTestProgressKind
{
    PhaseChanged,
    Rate,
    Completed
}

public sealed class SpeedTestOptions
{
    public Func<Uri>? DownloadUrlBuilder { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(15);
    public int ParallelStreams { get; init; } = 4;
    public int BufferSizeBytes { get; init; } = 256 * 1024;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(120);

    public void Validate()
    {
        if (DownloadUrlBuilder == null) throw new InvalidOperationException("DownloadUrlBuilder is required.");
        if (Duration <= TimeSpan.Zero) throw new InvalidOperationException("Duration must be > 0.");
        if (ParallelStreams <= 0 || ParallelStreams > 32) throw new InvalidOperationException("ParallelStreams must be in [1..32].");
        if (BufferSizeBytes < 8 * 1024) throw new InvalidOperationException("BufferSizeBytes too small.");
        if (ProgressInterval < TimeSpan.FromMilliseconds(50)) throw new InvalidOperationException("ProgressInterval too small.");
    }
}

