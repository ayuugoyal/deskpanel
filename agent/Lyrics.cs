using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskPanel.Agent;

sealed record LyricLine(long T, string X);

/// <summary>
/// Time-synced lyrics for whatever is playing, from LRCLIB (free, no key). Only the cleaned-up song title and
/// artist are sent. Results are cached per track; when nothing is found the phone shows its time-lapse instead.
/// </summary>
sealed partial class Lyrics
{
    static readonly HttpClient Http = CreateClient();
    readonly object gate = new();
    readonly Dictionary<string, List<LyricLine>?> cache = new();
    string? currentKey;
    const int MaxCached = 200;

    public int Version { get; private set; }
    public List<LyricLine>? Lines { get; private set; }
    public event Action? Changed;

    /// <summary>Version and lines read together, so a phone never gets a new version number with old lines.</summary>
    public (int Version, List<LyricLine>? Lines) Current()
    {
        lock (gate) return (Version, Lines);
    }

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeskPanel/1.0 (desk panel lyrics)");
        return c;
    }

    public void Track(string title, string artist, double duration)
    {
        var key = $"{title}|{artist}";
        lock (gate)
        {
            if (key == currentKey) return;
            currentKey = key;
            if (cache.TryGetValue(key, out var cached)) { Lines = cached; Version++; return; }
            Lines = null;
            Version++;
        }
        _ = Task.Run(async () =>
        {
            List<LyricLine>? lines = null;
            bool failed = false;
            try { lines = await Fetch(title, artist, duration); }
            catch (Exception e) { failed = true; Log.Error("lyrics", e); }
            lock (gate)
            {
                // only remember real answers: a network error must not turn into "no lyrics" until restart
                if (!failed)
                {
                    if (cache.Count >= MaxCached) cache.Clear();
                    cache[key] = lines;
                }
                if (currentKey != key) return;
                Lines = lines;
                Version++;
            }
            if (failed) _ = RetryLater(key);
            Log.Info($"lyrics for \"{title}\": {(lines == null ? "none" : lines.Count + " lines")}");
            Changed?.Invoke();
        });
    }

    /// <summary>After a failed lookup, let the same track be looked up again in a minute.</summary>
    async Task RetryLater(string key)
    {
        await Task.Delay(TimeSpan.FromMinutes(1));
        lock (gate) if (currentKey == key) currentKey = null;
    }

    public void Clear()
    {
        lock (gate)
        {
            if (currentKey == null) return;
            currentKey = null;
            Lines = null;
            Version++;
        }
    }

    static async Task<List<LyricLine>?> Fetch(string rawTitle, string rawArtist, double duration)
    {
        var (artist, track) = Clean(rawTitle, rawArtist);
        if (track.Length == 0) return null;

        var urls = new List<string>();
        if (artist.Length > 0)
            urls.Add($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(track)}&artist_name={Uri.EscapeDataString(artist)}");
        urls.Add($"https://lrclib.net/api/search?q={Uri.EscapeDataString((artist + " " + track).Trim())}");
        // A title-only search can match another artist's song, so use it only when the duration can confirm the match.
        if (artist.Length > 0 && duration > 0) urls.Add($"https://lrclib.net/api/search?q={Uri.EscapeDataString(track)}");

        bool anyAnswered = false;
        Exception? lastError = null;
        foreach (var url in urls)
        {
            string body;
            try { body = await Http.GetStringAsync(url); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { lastError = e; continue; }
            anyAnswered = true;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            JsonElement? best = null;
            double bestDiff = double.MaxValue;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (!r.TryGetProperty("syncedLyrics", out var sl) || sl.ValueKind != JsonValueKind.String) continue;
                double d = r.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : 0;
                double diff = duration > 0 && d > 0 ? Math.Abs(d - duration) : 0;
                if (duration > 0 && d > 0 && diff > 20) continue; // a different version of the song
                if (diff < bestDiff) { bestDiff = diff; best = r; }
            }
            if (best is { } b)
            {
                var lines = ParseLrc(b.GetProperty("syncedLyrics").GetString() ?? "");
                if (lines.Count > 0) return lines;
            }
        }
        // every query failed: report it, so the caller retries later instead of remembering "no lyrics"
        if (!anyAnswered && lastError != null) throw lastError;
        return null;
    }

    public static (string artist, string track) Clean(string title, string artist)
    {
        var t = title;
        t = Brackets().Replace(t, " ");
        t = Pipes().Split(t)[0];
        t = Noise().Replace(t, " ");
        t = Feat().Replace(t, "");
        var a = artist;
        var m = Dash().Match(t);
        if (m.Success) { a = m.Groups[1].Value; t = m.Groups[2].Value; }
        a = Topic().Replace(a, "");
        a = ArtistSplit().Split(a)[0];
        return (Spaces().Replace(a, " ").Trim(), Spaces().Replace(t, " ").Trim(' ', '-', '"', '\''));
    }

    static List<LyricLine> ParseLrc(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            var stamps = Stamp().Matches(line);
            if (stamps.Count == 0) continue;
            var text = Stamp().Replace(line, "").Trim();
            foreach (Match s in stamps)
            {
                var ms = (long)(int.Parse(s.Groups[1].Value) * 60000
                    + double.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture) * 1000);
                list.Add(new LyricLine(ms, text));
            }
        }
        list.Sort((x, y) => x.T.CompareTo(y.T));
        return list;
    }

    [GeneratedRegex(@"\s*[\(\[【].*?[\)\]】]")] private static partial Regex Brackets();
    [GeneratedRegex(@"\s+[|/]{1,2}\s+")] private static partial Regex Pipes();
    [GeneratedRegex(@"(?i)\b(official\s+(music\s+)?(video|audio|lyrics?\s+video)|lyric(al)?\s+(video|visuali[sz]er)|lyrics?|full\s+(video|song|audio)|audio|video|hd|4k|prod\.?\s.*$)")] private static partial Regex Noise();
    [GeneratedRegex(@"(?i)\s+(ft\.?|feat\.?|featuring)\s+.*$")] private static partial Regex Feat();
    [GeneratedRegex(@"^(.*?)\s+[-–—]\s+(.*)$")] private static partial Regex Dash();
    [GeneratedRegex(@"(?i)\s*-\s*topic$|vevo$")] private static partial Regex Topic();
    [GeneratedRegex(@"\s*(,|&|\sx\s|\sand\s)\s*")] private static partial Regex ArtistSplit();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")] private static partial Regex Stamp();
}
