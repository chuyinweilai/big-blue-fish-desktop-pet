using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

internal sealed class CompanionWeather : IDisposable
{
    private readonly HttpClient web;
    private readonly string? cachePath;
    private readonly Func<DateTimeOffset> clock;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Entry> entries = new();
    private readonly Dictionary<string, (DateTimeOffset At, string Text)> briefings = new();
    private readonly Dictionary<string, DateTimeOffset> attempts = new();
    private readonly Dictionary<string, Task> pending = new();
    internal sealed record Entry(string Code, DateTimeOffset FetchedAt, DateTimeOffset ObservedAt, string Context);

    internal CompanionWeather(HttpClient web, string? cachePath = null, Func<DateTimeOffset>? clock = null)
    {
        this.web = web;
        this.cachePath = cachePath == null ? null : Path.GetFullPath(cachePath);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (this.cachePath != null)
        {
            try
            {
                foreach (var entry in JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(this.cachePath)) ?? [])
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.Code) && !string.IsNullOrWhiteSpace(entry.Context))
                        entries[entry.Code] = entry;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private bool Fresh(Entry entry, DateTimeOffset now) =>
        now >= entry.FetchedAt && now >= entry.ObservedAt
        && now - entry.FetchedAt <= TimeSpan.FromHours(6)
        && now - entry.ObservedAt <= TimeSpan.FromHours(6);

    // Called by the normal UI timer; no HTTP or disk work runs on the UI thread.
    internal Task RefreshIfDue(string regionCode)
    {
        var region = CompanionRegions.District(regionCode);
        lock (sync)
        {
            if (lifetime.IsCancellationRequested) return Task.CompletedTask;
            if (pending.TryGetValue(region.Code, out var active) && !active.IsCompleted) return active;
            var now = clock();
            var last = attempts.TryGetValue(region.Code, out var attempt) ? attempt
                : entries.TryGetValue(region.Code, out var cached) ? cached.FetchedAt : DateTimeOffset.MinValue;
            if (now >= last && now - last < TimeSpan.FromHours(1)) return Task.CompletedTask;
            attempts[region.Code] = now;
            return pending[region.Code] = Task.Run(() => Fetch(region));
        }
    }

    internal string CachedContext(string regionCode)
    {
        var code = CompanionRegions.District(regionCode).Code;
        lock (sync)
            return entries.TryGetValue(code, out var entry) && Fresh(entry, clock()) ? entry.Context : "";
    }

    internal async Task<string> Context(string regionCode, CancellationToken token)
    {
        var refresh = RefreshIfDue(regionCode);
        var cached = CachedContext(regionCode);
        if (cached.Length > 0) return cached;
        await refresh.WaitAsync(token);
        cached = CachedContext(regionCode);
        if (cached.Length == 0) throw new IOException("暂时没有六小时内的天气资料，请稍后重试。");
        return cached;
    }

    public void Dispose() { lock (sync) lifetime.Cancel(); }


    internal static string Description(int code) => code switch
    {
        0 => "晴朗", 1 => "大部晴朗", 2 => "局部多云", 3 => "阴天",
        45 or 48 => "有雾", 51 or 53 or 55 => "毛毛雨", 56 or 57 => "冻毛毛雨",
        61 => "小雨", 63 => "中雨", 65 => "大雨", 66 or 67 => "冻雨",
        71 => "小雪", 73 => "中雪", 75 => "大雪", 77 => "雪粒",
        80 => "小阵雨", 81 => "阵雨", 82 => "强阵雨", 85 or 86 => "阵雪",
        95 => "雷雨", 96 or 99 => "雷雨伴冰雹", _ => "天气状况未提供"
    };

    internal static string ForecastUrl(CompanionRegion region) =>
        "https://api.open-meteo.com/v1/forecast?latitude=" + region.Latitude!.Value.ToString(CultureInfo.InvariantCulture)
        + "&longitude=" + region.Longitude!.Value.ToString(CultureInfo.InvariantCulture)
        + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m"
        + "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max"
        + "&forecast_days=1&timezone=Asia%2FShanghai";

    // Multi-day briefing used when the owner asks about a specific day: it adds
    // sunshine hours, cloud cover, UV and rain totals so replies can quote numbers.
    internal static string BriefingUrl(CompanionRegion region, int days) =>
        "https://api.open-meteo.com/v1/forecast?latitude=" + region.Latitude!.Value.ToString(CultureInfo.InvariantCulture)
        + "&longitude=" + region.Longitude!.Value.ToString(CultureInfo.InvariantCulture)
        + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m"
        + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum"
        + ",daylight_duration,cloud_cover_mean,uv_index_max,wind_speed_10m_max,sunrise,sunset"
        + "&forecast_days=" + days.ToString(CultureInfo.InvariantCulture) + "&timezone=Asia%2FShanghai";

    internal async Task<string> ForecastBriefing(string regionCode, int days, CancellationToken token)
    {
        var region = CompanionRegions.District(regionCode);
        if (region.Latitude == null || region.Longitude == null) throw new IOException("所选地点缺少坐标，无法查询天气。");
        days = Math.Clamp(days, 1, 7);
        var key = region.Code + ":" + days.ToString(CultureInfo.InvariantCulture);
        lock (sync)
            if (briefings.TryGetValue(key, out var hit) && clock() - hit.At <= TimeSpan.FromMinutes(30) && hit.Text.Length > 0)
                return hit.Text;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await web.GetAsync(BriefingUrl(region, days), deadline.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var text = FormatBriefing(region, json.RootElement);
        lock (sync) briefings[key] = (clock(), text);
        return text;
    }

    internal static string FormatBriefing(CompanionRegion region, JsonElement root)
    {
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        string Number(JsonElement value) => value.GetDouble().ToString("0.#", CultureInfo.InvariantCulture);
        string? Text(JsonElement array, int index) =>
            array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.String
                ? array[index].GetString() : null;
        double? Hours(JsonElement array, int index) =>
            array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number
                ? Math.Round(array[index].GetDouble() / 3600, 1) : null;
        double? Value(JsonElement array, int index) =>
            array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number
                ? array[index].GetDouble() : null;

        var lines = new List<string>
        {
            $"地点：{CompanionRegions.FullName(region)}。查询时间：{current.GetProperty("time").GetString()}（北京时间）。"
            + $"此刻{Description(current.GetProperty("weather_code").GetInt32())}，气温{Number(current.GetProperty("temperature_2m"))}℃，"
            + $"体感{Number(current.GetProperty("apparent_temperature"))}℃，湿度{Number(current.GetProperty("relative_humidity_2m"))}%，"
            + $"风速{Number(current.GetProperty("wind_speed_10m"))}公里/小时。"
        };
        var names = new[] { "今天", "明天", "后天" };
        var dates = daily.GetProperty("time");
        var count = Math.Min(dates.GetArrayLength(), 3);
        for (int i = 0; i < count; i++)
        {
            var label = i < names.Length ? names[i] : ("第" + (i + 1) + "天");
            var parts = new List<string>
            {
                $"{label}（{dates[i].GetString()}）：{Description(daily.GetProperty("weather_code")[i].GetInt32())}",
                $"最低{Number(daily.GetProperty("temperature_2m_min")[i])}℃、最高{Number(daily.GetProperty("temperature_2m_max")[i])}℃",
                $"降水概率{Number(daily.GetProperty("precipitation_probability_max")[i])}%、降水量{Number(daily.GetProperty("precipitation_sum")[i])}毫米"
            };
            // Sunshine itself is not published for this grid, and the provider's
            // sunshine_duration just repeats daylight length, so daylight hours
            // plus mean cloud cover are the honest way to describe the sun.
            if (Hours(daily.GetProperty("daylight_duration"), i) is { } daylight) parts.Add($"白天{daylight.ToString("0.#", CultureInfo.InvariantCulture)}小时");
            if (Value(daily.GetProperty("cloud_cover_mean"), i) is { } cloud) parts.Add($"平均云量{cloud.ToString("0", CultureInfo.InvariantCulture)}%");
            if (Value(daily.GetProperty("uv_index_max"), i) is { } uv) parts.Add($"紫外线指数最高{uv.ToString("0.#", CultureInfo.InvariantCulture)}");
            if (Value(daily.GetProperty("wind_speed_10m_max"), i) is { } wind) parts.Add($"最大风速{wind.ToString("0.#", CultureInfo.InvariantCulture)}公里/小时");
            if (Text(daily.GetProperty("sunrise"), i) is { } sunrise && Text(daily.GetProperty("sunset"), i) is { } sunset)
                parts.Add($"日出{sunrise[^5..]}、日落{sunset[^5..]}");
            lines.Add(string.Join("，", parts) + "。");
        }
        return string.Join("\n", lines);
    }

    private async Task Fetch(CompanionRegion region)
    {
        try
        {
            if (region.Latitude == null || region.Longitude == null) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var token = deadline.Token;
            using var response = await web.GetAsync(ForecastUrl(region), token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var current = json.RootElement.GetProperty("current");
            var daily = json.RootElement.GetProperty("daily");
            var observed = new DateTimeOffset(DateTime.Parse(current.GetProperty("time").GetString()!,
                CultureInfo.InvariantCulture, DateTimeStyles.None), TimeSpan.FromHours(8));
            string Number(JsonElement value) => value.GetDouble().ToString("0.#", CultureInfo.InvariantCulture);
            var context = $"地点：{CompanionRegions.FullName(region)}（区县中心附近的天气估算）。"
                + $"天气资料记录时间：{current.GetProperty("time").GetString()}（北京时间）。"
                + $"记录时{Description(current.GetProperty("weather_code").GetInt32())}，"
                + $"气温{Number(current.GetProperty("temperature_2m"))}℃，体感{Number(current.GetProperty("apparent_temperature"))}℃，"
                + $"相对湿度{Number(current.GetProperty("relative_humidity_2m"))}%，风速{Number(current.GetProperty("wind_speed_10m"))}公里/小时。"
                + $"资料日期{observed:yyyy-MM-dd}最低{Number(daily.GetProperty("temperature_2m_min")[0])}℃、最高{Number(daily.GetProperty("temperature_2m_max")[0])}℃，"
                + $"最高降水概率{Number(daily.GetProperty("precipitation_probability_max")[0])}%。";
            var entry = new Entry(region.Code, clock(), observed, context);
            lock (sync)
            {
                if (lifetime.IsCancellationRequested || !Fresh(entry, clock())) return;
                entries[region.Code] = entry;
                if (cachePath != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    var temporary = cachePath + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new List<Entry>(entries.Values)));
                    File.Move(temporary, cachePath, overwrite: true);
                }
            }
        }
        // A failed hourly refresh retains the last good entry, never extending its lifetime.
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
            or OperationCanceledException or JsonException or InvalidOperationException or FormatException
            or KeyNotFoundException or ArgumentException or ObjectDisposedException) { }
    }
}
