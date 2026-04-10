using System.Text.Json.Serialization;

namespace ReFlow.Models;

public sealed class SpeedTestSettings
{
    /// <summary>Test duration in seconds.</summary>
    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 15;

    /// <summary>Start download test when the main window is shown (including opening from tray).</summary>
    [JsonPropertyName("autoStartWhenWindowOpen")]
    public bool AutoStartWhenWindowOpen { get; set; }

    /// <summary>When the app starts minimized to tray, start background quick test automatically.</summary>
    [JsonPropertyName("autoStartOnTrayStartup")]
    public bool AutoStartOnTrayStartup { get; set; } = true;
}

