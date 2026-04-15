using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReFlow.Models;

namespace ReFlow.Services;

public static class SettingsStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonDocumentOptions LenientDocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonNodeOptions LenientNodeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string SettingsFileName = "ReFlow_settings.json";
    private const string LegacySettingsFileName = "settings.json";
    private const string SettingsFileHeaderLine = "Это файл настроек программы ReFlow";

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReFlow");

    public static string SettingsPath => Path.Combine(SettingsDirectory, SettingsFileName);

    private static string GetProgramDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var fileName = Path.GetFileName(processPath);
            if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(AppContext.BaseDirectory);

            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
                return Path.GetFullPath(dir);
        }

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static void TryMigrateFromProgramDirectory()
    {
        if (File.Exists(SettingsPath))
            return;

        var oldDir = GetProgramDirectory();
        var newDir = Path.GetFullPath(SettingsDirectory);
        if (string.Equals(Path.GetFullPath(oldDir), newDir, StringComparison.OrdinalIgnoreCase))
            return;

        var oldMain = Path.Combine(oldDir, SettingsFileName);
        var oldLegacy = Path.Combine(oldDir, LegacySettingsFileName);

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            if (File.Exists(oldMain))
            {
                File.Copy(oldMain, SettingsPath, overwrite: false);
                return;
            }

            if (!File.Exists(oldLegacy))
                return;

            var rawLegacy = File.ReadAllText(oldLegacy);
            var jsonLegacy = ExtractJsonFromSettingsFile(rawLegacy);
            var fromLegacy = DeserializeSettingsOrMigrate(jsonLegacy, out _);
            if (fromLegacy != null)
            {
                Normalize(fromLegacy);
                MigrateAutostartFromRegistryIfNeeded(fromLegacy);
                Save(fromLegacy);
            }
        }
        catch
        {
            // при ошибке миграции сработает обычная логика Load
        }
    }

    public static AppSettings Load()
    {
        try
        {
            TryMigrateFromProgramDirectory();

            if (!File.Exists(SettingsPath))
            {
                var legacyPath = Path.Combine(SettingsDirectory, LegacySettingsFileName);
                if (File.Exists(legacyPath))
                {
                    var rawLegacy = File.ReadAllText(legacyPath);
                    var jsonLegacy = ExtractJsonFromSettingsFile(rawLegacy);
                    var fromLegacy = DeserializeSettingsOrMigrate(jsonLegacy, out _);
                    var mergedLegacy = MergeWithDefaults(fromLegacy);
                    Save(mergedLegacy);
                    return mergedLegacy;
                }

                var fresh = AppSettings.CreateDefault();
                Save(fresh);
                return fresh;
            }

            var raw = File.ReadAllText(SettingsPath);
            var json = ExtractJsonFromSettingsFile(raw);
            AppSettings loaded;
            bool wasFlatLegacy;
            try
            {
                loaded = DeserializeSettingsOrMigrate(json, out wasFlatLegacy);
            }
            catch
            {
                var sanitized = StripTrailingCommas(json);
                try
                {
                    loaded = DeserializeSettingsOrMigrate(sanitized, out wasFlatLegacy);
                }
                catch
                {
                    BackupCorruptedFile();
                    loaded = AppSettings.CreateDefault();
                    wasFlatLegacy = false;
                }
            }

            if (loaded == null)
                loaded = AppSettings.CreateDefault();

            var formatVersionBefore = loaded.SettingsFormatVersion;
            Normalize(loaded);
            MigrateAutostartFromRegistryIfNeeded(loaded);
            if (wasFlatLegacy || formatVersionBefore < 7)
                Save(loaded);
            return loaded;
        }
        catch
        {
            BackupCorruptedFile();
            return AppSettings.CreateDefault();
        }
    }

    private static void BackupCorruptedFile()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var backupPath = SettingsPath + ".bak";
            File.Copy(SettingsPath, backupPath, overwrite: true);
        }
        catch { /* best effort */ }
    }

    private static void MigrateAutostartFromRegistryIfNeeded(AppSettings settings)
    {
        if (settings.AutostartEnabled)
            return;
        if (!AutostartService.IsEnabled())
            return;

        settings.AutostartEnabled = true;
        Save(settings);
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        Normalize(settings);

        JsonObject root;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var rawExisting = File.ReadAllText(SettingsPath);
                var json = ExtractJsonFromSettingsFile(rawExisting);
                root = ParseJsonLenient(json);
            }
            else
            {
                root = new JsonObject();
            }
        }
        catch
        {
            root = new JsonObject();
        }

        var modelNode = JsonSerializer.SerializeToNode(settings, JsonOptions);
        if (modelNode is JsonObject modelObj)
        {
            MergeModelIntoExistingJson(root, modelObj);
            StripLegacyJsonKeys(root);
        }
        else
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, SettingsFileHeaderLine + Environment.NewLine + json);
            return;
        }

        var outOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var outJson = root.ToJsonString(outOpts);
        File.WriteAllText(SettingsPath, SettingsFileHeaderLine + Environment.NewLine + outJson);
    }

    private static void MergeModelIntoExistingJson(JsonObject root, JsonObject model)
    {
        foreach (var kvp in model)
        {
            if (kvp.Value is JsonObject modelChild)
            {
                if (root.TryGetPropertyValue(kvp.Key, out var existing) && existing is JsonObject existingObj)
                    MergeModelIntoExistingJson(existingObj, modelChild);
                else
                    root[kvp.Key] = CloneNode(kvp.Value);
            }
            else
                root[kvp.Key] = CloneNode(kvp.Value);
        }
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        if (node is null)
            return null;
        return JsonNode.Parse(node.ToJsonString())!;
    }

    /// <summary>Удаляет ключи старого приложения (профили дисплея, советы, displayMode).</summary>
    private static void StripLegacyJsonKeys(JsonObject root)
    {
        root.Remove("profiles");
        root.Remove("displayMode");
        root.Remove("advice");
        if (root.TryGetPropertyValue("tray", out var trayNode) && trayNode is JsonObject tray)
        {
            tray.Remove("showResolutionListInTrayMenu");
            tray.Remove("showProfileNamesInTrayMenu");
        }
    }

    private static JsonObject ParseJsonLenient(string json)
    {
        try
        {
            var parsed = JsonNode.Parse(json, LenientNodeOptions, LenientDocOptions);
            return parsed as JsonObject ?? new JsonObject();
        }
        catch
        {
            var sanitized = StripTrailingCommas(json);
            try
            {
                var parsed = JsonNode.Parse(sanitized, LenientNodeOptions, LenientDocOptions);
                return parsed as JsonObject ?? new JsonObject();
            }
            catch
            {
                return new JsonObject();
            }
        }
    }

    private static string StripTrailingCommas(string json) =>
        Regex.Replace(json, @",\s*([}\]])", "$1");

    private static string ExtractJsonFromSettingsFile(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('{'))
            return trimmed;

        var lines = raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lines.Length < 2)
            return trimmed;

        return string.Join(Environment.NewLine, lines.Skip(1)).TrimStart();
    }

    private static AppSettings DeserializeSettingsOrMigrate(string json, out bool wasFlatLegacy)
    {
        using var doc = JsonDocument.Parse(json, LenientDocOptions);
        var root = doc.RootElement;

        if (root.TryGetProperty("startupAndWindow", out _)
            || root.TryGetProperty("tray", out _)
            || root.TryGetProperty("speedTest", out _)
            || root.TryGetProperty("displayMode", out _)
            || root.TryGetProperty("profiles", out _))
        {
            wasFlatLegacy = false;
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault();
        }

        wasFlatLegacy = true;
        var flat = JsonSerializer.Deserialize<AppSettingsFlatLegacy>(json, JsonOptions);
        return flat != null ? AppSettings.FromFlatLegacy(flat) : AppSettings.CreateDefault();
    }

    private static AppSettings MergeWithDefaults(AppSettings? partial)
    {
        var d = AppSettings.CreateDefault();
        if (partial == null)
            return d;

        partial.StartupAndWindow ??= d.StartupAndWindow;
        partial.Tray ??= d.Tray;
        partial.Ui ??= d.Ui;
        partial.Meta ??= d.Meta;
        partial.SpeedTest ??= d.SpeedTest;
        Normalize(partial);
        return partial;
    }

    private static void Normalize(AppSettings s)
    {
        s.StartupAndWindow ??= new StartupWindowSettings();
        s.Tray ??= new TrayMenuSettings();
        s.Ui ??= new UiAppearanceSettings();
        s.Meta ??= new SettingsMeta();
        s.SpeedTest ??= new SpeedTestSettings();

        if (!Enum.IsDefined(typeof(UiTheme), s.UiTheme))
            s.UiTheme = UiTheme.Dark;
        s.UiTheme = UiTheme.Dark;

        s.UiLanguage = AppLanguageCatalog.Normalize(s.UiLanguage);

        if (s.SpeedTest.DurationSeconds <= 0)
            s.SpeedTest.DurationSeconds = 10;
        if (s.SpeedTest.ManualDurationSeconds <= 0)
            s.SpeedTest.ManualDurationSeconds = 10;
        s.SpeedTest.DownloadFileUrl = string.IsNullOrWhiteSpace(s.SpeedTest.DownloadFileUrl)
            ? null
            : s.SpeedTest.DownloadFileUrl.Trim();
        if (s.SpeedTest.DownloadFileUrl == null)
            s.SpeedTest.UseCustomDownloadFileUrl = false;

        if (s.SettingsFormatVersion < 7)
            s.SettingsFormatVersion = 7;

        if (s.SettingsFormatVersion < 8)
        {
            // В JSON до v8 полей не было: десериализатор даёт false для обоих — задаём осмысленные умолчания.
            s.SpeedTest.AutoStartWhenWindowOpen = false;
            s.SpeedTest.AutoStartOnTrayStartup = true;
            s.SettingsFormatVersion = 8;
        }

        if (s.SettingsFormatVersion < 9)
        {
            if (s.Tray.SingleClickAction == TrayIconClickAction.OpenWindow)
                s.Tray.ClickPairing = TrayClickPairing.WindowSingle_QuickTestDouble;
            else
                s.Tray.ClickPairing = TrayClickPairing.QuickTestSingle_WindowDouble;
            s.SettingsFormatVersion = 9;
        }

        s.Tray.SingleClickAction = s.Tray.ClickPairing == TrayClickPairing.WindowSingle_QuickTestDouble
            ? TrayIconClickAction.OpenWindow
            : TrayIconClickAction.QuickTest;
    }
}
