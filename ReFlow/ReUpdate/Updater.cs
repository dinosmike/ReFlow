using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace ReUpdate;

/// <summary>
/// Тихое автообновление из релизов GitHub: скачать новый exe рядом, подменить себя
/// (запущенный exe можно переименовать, но не перезаписать) и перезапуститься.
/// </summary>
public static class Updater
{
    /// <summary>Аргумент перезапуска после обновления: программа стартует тихо в трее.</summary>
    public const string UpdatedArg = "--updated";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    /// <summary>Программа занята — обновление подождёт.</summary>
    private static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(10);

    private static DispatcherTimer? _timer;
    private static bool _running;

    /// <summary>Путь к своему exe, только если это опубликованная одним файлом сборка (в отладке — null).</summary>
    private static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return null;
            // Рядом с dll — это обычная сборка из bin, а не single-file.
            return File.Exists(Path.ChangeExtension(path, ".dll")) ? null : path;
        }
    }

    /// <summary>Убрать exe прошлой версии, оставшийся после подмены. Вызывать при старте.</summary>
    public static void CleanupAfterUpdate()
    {
        var exe = ExePath;
        if (exe == null)
            return;
        try { File.Delete(exe + ".old"); } catch { /* ещё занят — удалим в следующий раз */ }
        try { File.Delete(exe + ".new"); } catch { /* ignore */ }
    }

    public static void Start(Dispatcher dispatcher)
    {
        if (ExePath == null)
            return;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            _timer.Interval = await CheckAndUpdateAsync() ? BusyRetry : CheckInterval;
            _timer.Start();
        };
        _timer.Start();
    }

    /// <summary><c>true</c> — обновление есть, но отложено (программа занята); проверить снова пораньше.</summary>
    private static async Task<bool> CheckAndUpdateAsync()
    {
        if (_running || !ReUpdateConfig.AutoUpdateEnabled())
            return false;
        var exe = ExePath;
        if (exe == null)
            return false;

        _running = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(ReUpdateConfig.Repo + "-Updater");

            var api = $"https://api.github.com/repos/{ReUpdateConfig.Owner}/{ReUpdateConfig.Repo}/releases/latest";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(api));
            var latest = VersionText.Parse(doc.RootElement.GetProperty("tag_name").GetString() ?? "");
            if (latest == null || latest <= ReUpdateConfig.CurrentVersion)
                return false;

            // Сначала отдельный файл для обновлений (его счётчик скачиваний = число обновлений), иначе обычный exe.
            string? url = null, fallbackUrl = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (string.Equals(name, ReUpdateConfig.UpdateAssetName, StringComparison.OrdinalIgnoreCase))
                    url = asset.GetProperty("browser_download_url").GetString();
                else if (string.Equals(name, ReUpdateConfig.AssetName, StringComparison.OrdinalIgnoreCase))
                    fallbackUrl = asset.GetProperty("browser_download_url").GetString();
            }
            url ??= fallbackUrl;
            if (url == null)
                return false;

            if (ReUpdateConfig.IsBusy())
                return true;

            var newPath = exe + ".new";
            await using (var src = await http.GetStreamAsync(url))
            await using (var dst = File.Create(newPath))
                await src.CopyToAsync(dst);
            if (!LooksLikeExe(newPath))
            {
                File.Delete(newPath);
                return false;
            }

            if (ReUpdateConfig.IsBusy())
                return true;

            var oldPath = exe + ".old";
            if (File.Exists(oldPath))
                File.Delete(oldPath);
            File.Move(exe, oldPath);
            try
            {
                File.Move(newPath, exe);
            }
            catch
            {
                File.Move(oldPath, exe);
                throw;
            }

            Restart(exe);
            return false;
        }
        catch
        {
            // Нет сети, лимит GitHub, папка только для чтения — тихо пробуем в следующий раз.
            return false;
        }
        finally
        {
            _running = false;
        }
    }

    private static bool LooksLikeExe(string path)
    {
        var info = new FileInfo(path);
        if (info.Length < 1_000_000)
            return false;
        using var f = File.OpenRead(path);
        return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
    }

    /// <summary>
    /// Новый процесс стартует с паузой: пока старый не вышел, он держит мьютекс одного экземпляра
    /// и новый просто активировал бы его окно.
    /// </summary>
    private static void Restart(string exe)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping -n 4 127.0.0.1 >nul & start \"\" \"{exe}\" {UpdatedArg}",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        System.Windows.Application.Current.Shutdown();
    }
}

internal static class VersionText
{
    /// <summary>«ReFlow_1_2», «v1.2», «1.2.3» → версия.</summary>
    public static Version? Parse(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)[._](\d+)(?:[._](\d+))?");
        if (!m.Success)
            return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }
}
