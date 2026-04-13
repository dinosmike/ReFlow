using System.Text.Json.Serialization;

namespace ReFlow.Models;

public sealed class SpeedTestSettings
{
    /// <summary>Test duration in seconds.</summary>
    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 10;

    /// <summary>Start download test when the main window is shown (including opening from tray).</summary>
    [JsonPropertyName("autoStartWhenWindowOpen")]
    public bool AutoStartWhenWindowOpen { get; set; }

    /// <summary>On app startup, start the tray background quick test automatically (independent of window visibility).</summary>
    [JsonPropertyName("autoStartOnTrayStartup")]
    public bool AutoStartOnTrayStartup { get; set; } = true;
}

