using System.Text.Json.Serialization;

namespace ReFlow.Models;

public sealed class SpeedTestSettings
{
    /// <summary>Test duration in seconds.</summary>
    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 10;

    /// <summary>Last manual duration value shown in settings (seconds).</summary>
    [JsonPropertyName("manualDurationSeconds")]
    public int ManualDurationSeconds { get; set; } = 10;

    /// <summary>Optional override for the speed test download URL (if empty/null, <see cref="SpeedTestConfig.DownloadFileUrl"/> is used).</summary>
    [JsonPropertyName("downloadFileUrl")]
    public string? DownloadFileUrl { get; set; }

    /// <summary>Start download test when the main window is shown (including opening from tray).</summary>
    [JsonPropertyName("autoStartWhenWindowOpen")]
    public bool AutoStartWhenWindowOpen { get; set; }

    /// <summary>On app startup, start the tray background quick test automatically (independent of window visibility).</summary>
    [JsonPropertyName("autoStartOnTrayStartup")]
    public bool AutoStartOnTrayStartup { get; set; } = true;
}

