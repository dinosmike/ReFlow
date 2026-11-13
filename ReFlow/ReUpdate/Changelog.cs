using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReUpdate;

/// <summary>Одна версия из changelog.json.</summary>
public sealed class ChangelogVersion
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("ru")] public List<string> Ru { get; set; } = new();
    [JsonPropertyName("en")] public List<string> En { get; set; } = new();

    internal Version? Parsed => VersionText.Parse(Version);
    internal IReadOnlyList<string> Items => ReUpdateConfig.IsRussian || En.Count == 0 ? Ru : En;
}

internal sealed class ChangelogFile
{
    [JsonPropertyName("versions")] public List<ChangelogVersion> Versions { get; set; } = new();
}

/// <summary>
/// История версий: свежая — с GitHub (raw changelog.json), без сети — последняя скачанная,
/// а если её нет — копия, вшитая в exe при сборке.
/// </summary>
public static class Changelog
{
    private const string CacheFile = "changelog-cache.json";

    public static async Task<List<ChangelogVersion>> LoadAsync()
    {
        var cachePath = Path.Combine(ReUpdateConfig.DataDir, CacheFile);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var url = $"https://raw.githubusercontent.com/{ReUpdateConfig.Owner}/{ReUpdateConfig.Repo}/{ReUpdateConfig.Branch}/changelog.json";
            var json = await http.GetStringAsync(url);
            var fresh = Parse(json);
            if (fresh.Count > 0)
            {
                try { await File.WriteAllTextAsync(cachePath, json); } catch { /* ignore */ }
                return fresh;
            }
        }
        catch
        {
            // Нет сети — ниже кэш или вшитая копия.
        }

        try
        {
            if (File.Exists(cachePath))
            {
                var cached = Parse(await File.ReadAllTextAsync(cachePath));
                if (cached.Count > 0)
                    return cached;
            }
        }
        catch { /* ignore */ }

        return LoadEmbedded();
    }

    private static List<ChangelogVersion> LoadEmbedded()
    {
        var asm = Assembly.GetEntryAssembly();
        var name = asm?.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("changelog.json", StringComparison.OrdinalIgnoreCase));
        if (asm == null || name == null)
            return new();
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    private static List<ChangelogVersion> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ChangelogFile>(json);
        return (file?.Versions ?? new())
            .Where(v => v.Parsed != null && v.Ru.Count + v.En.Count > 0)
            .OrderByDescending(v => v.Parsed)
            .ToList();
    }
}

/// <summary>
/// «Есть непрочитанное "Что нового"»: после обновления версия программы больше, чем последняя
/// увиденная. При первом запуске (новый пользователь) отмечаем текущую как увиденную — точки нет.
/// </summary>
public static class WhatsNewState
{
    private const string StateFile = "reupdate-state.json";

    public static event Action? Changed;

    public static Version? LastSeen { get; private set; }

    public static bool HasUnseen => LastSeen != null && ReUpdateConfig.CurrentVersion > LastSeen;

    public static void Initialize()
    {
        try
        {
            var path = Path.Combine(ReUpdateConfig.DataDir, StateFile);
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                LastSeen = VersionText.Parse(doc.RootElement.GetProperty("lastSeen").GetString() ?? "");
            }
        }
        catch { /* битый файл — как первый запуск */ }

        if (LastSeen == null)
            MarkSeen();
    }

    public static void MarkSeen()
    {
        LastSeen = ReUpdateConfig.CurrentVersion;
        try
        {
            File.WriteAllText(Path.Combine(ReUpdateConfig.DataDir, StateFile),
                JsonSerializer.Serialize(new { lastSeen = LastSeen.ToString(3) }));
        }
        catch { /* ignore */ }
        Changed?.Invoke();
    }
}
