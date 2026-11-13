using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ReFlow.Models;

public sealed class SpeedTestSettings
{
    /// <summary>Test duration in seconds.</summary>
    [JsonPropertyOrder(0)]
    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 10;

    /// <summary>Last manual duration value shown in settings (seconds).</summary>
    [JsonPropertyOrder(1)]
    [JsonPropertyName("manualDurationSeconds")]
    public int ManualDurationSeconds { get; set; } = 5;

    /// <summary>Optional override for the speed test download URL (if empty/null, <see cref="SpeedTestConfig.DownloadFileUrl"/> is used).</summary>
    [JsonPropertyOrder(2)]
    [JsonPropertyName("downloadFileUrl")]
    public string? DownloadFileUrl { get; set; }

    /// <summary>History of previously used download URLs (most recent first).</summary>
    [JsonPropertyOrder(3)]
    [JsonPropertyName("downloadFileUrlHistory")]
    public List<string> DownloadFileUrlHistory { get; set; } = new();

    /// <summary>Whether to use <see cref="DownloadFileUrl"/> instead of the built-in default URL.</summary>
    [JsonPropertyOrder(4)]
    [JsonPropertyName("useCustomDownloadFileUrl")]
    public bool UseCustomDownloadFileUrl { get; set; }

    /// <summary>Start download test when the main window is shown (including opening from tray).</summary>
    [JsonPropertyOrder(5)]
    [JsonPropertyName("autoStartWhenWindowOpen")]
    public bool AutoStartWhenWindowOpen { get; set; }

    /// <summary>On app startup, start the tray background quick test automatically (independent of window visibility).</summary>
    [JsonPropertyOrder(6)]
    [JsonPropertyName("autoStartOnTrayStartup")]
    public bool AutoStartOnTrayStartup { get; set; }

    /// <summary>Сервер замера, выбранный правым кликом по табло (его URL); null — основной.</summary>
    [JsonPropertyOrder(7)]
    [JsonPropertyName("preferredServerUrl")]
    public string? PreferredServerUrl { get; set; }

    /// <summary>Если выбранный сервер не отвечает — перейти на следующий по списку; выключено — ждать выбранный.</summary>
    [JsonPropertyOrder(8)]
    [JsonPropertyName("autoFallbackServers")]
    public bool AutoFallbackServers { get; set; } = true;
}

