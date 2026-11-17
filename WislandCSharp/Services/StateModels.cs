using System;

namespace Wisland.Services;

/// <summary>媒体状态（对应 Python media.py 的 empty_state dict，改为强类型）。</summary>
public sealed class MediaState
{
    public bool HasSession { get; init; }
    public string AppId { get; init; } = "";
    public string AppName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string CoverPath { get; init; } = "";
    public string SongId { get; init; } = "";
    public bool Playing { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
    public bool CanPrev { get; init; }
    public bool CanNext { get; init; }
    public DateTime Stamp { get; init; } = DateTime.UtcNow;

    /// <summary>外推后的当前播放位置（秒）。</summary>
    public double PositionNow
    {
        get
        {
            var pos = Position;
            if (Playing) pos += (DateTime.UtcNow - Stamp).TotalSeconds;
            return Duration > 0 ? Math.Min(pos, Duration) : Math.Max(0, pos);
        }
    }

    public static MediaState Empty { get; } = new();
}

/// <summary>天气状态（对应 Python weather.py 的 updated dict，改为强类型）。</summary>
public sealed record WeatherState
{
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
    public string City { get; init; } = "";
    public double Temp { get; init; }
    public double FeelsLike { get; init; }
    public string Desc { get; init; } = "";
    public string Icon { get; init; } = "";
    public int Humidity { get; init; }
    public double Wind { get; init; }
    public int WindDeg { get; init; }
    public int Pressure { get; init; }
    public int Visibility { get; init; }
    public int Clouds { get; init; }
    public long Sunrise { get; init; }
    public long Sunset { get; init; }
    public int TzOffset { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public System.Collections.Generic.IReadOnlyList<HourlyWeather> Hourly { get; init; }
        = Array.Empty<HourlyWeather>();
    public System.Collections.Generic.IReadOnlyList<DailyWeather> Daily { get; init; }
        = Array.Empty<DailyWeather>();

    /// <summary>收起态展示用的温度文本（如 "18°"）。</summary>
    public string TempText => Ok ? $"{Math.Round(Temp)}°" : "--";

    public static WeatherState Empty { get; } = new();
}

public sealed class HourlyWeather
{
    public long Time { get; init; }
    public double Temp { get; init; }
    public string Icon { get; init; } = "";
    public int Pop { get; init; }
    public int Visibility { get; init; }
    public int Clouds { get; init; }
}

public sealed class DailyWeather
{
    public string Date { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public string Icon { get; init; } = "";
}