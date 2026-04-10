using System.Text.Json.Serialization;

namespace ReFlow.Models;

public sealed class SpeedTestSettings
{
    /// <summary>Direct URL to a large static file, e.g. https://example.com/speedtest.bin</summary>
    [JsonPropertyName("fileUrl")]
    public string? FileUrl { get; set; }

    /// <summary>Test duration in seconds.</summary>
    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 15;
}

