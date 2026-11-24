using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DeskPanel.Agent;

sealed record MediaInfo(string Title, string Artist, string App, bool Playing, double Position, double Duration);

/// <summary>Whatever is playing on the laptop (Spotify, browser, Media Player…), via Windows media controls.</summary>
sealed class MediaSource
{
    Task<GlobalSystemMediaTransportControlsSessionManager>? manager;
    string? artKey;
    int artTries;

    public int ArtVersion { get; private set; }
    public string? ArtBase64 { get; private set; }

    readonly object managerGate = new();

    async Task<GlobalSystemMediaTransportControlsSession?> Session()
    {
        Task<GlobalSystemMediaTransportControlsSessionManager> request;
        lock (managerGate) request = manager ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
        try
        {
            return (await request).GetCurrentSession();
        }
        catch
        {
            // do not keep a failed request forever: ask again next time
            lock (managerGate) if (ReferenceEquals(manager, request)) manager = null;
            throw;
        }
    }

    public async Task<MediaInfo?> Get()
    {
        var s = await Session();
        if (s == null)
        {
            if (artKey != null || ArtBase64 != null) { artKey = null; ArtBase64 = null; ArtVersion++; }
            return null;
        }

        var p = await s.TryGetMediaPropertiesAsync();
        var playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var tl = s.GetTimelineProperties();
        double duration = (tl.EndTime - tl.StartTime).TotalSeconds;
        double position = (tl.Position - tl.StartTime).TotalSeconds;
        if (playing && tl.LastUpdatedTime.Year > 2000) position += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
        if (duration <= 0 || duration > 86400) { duration = 0; position = 0; }
        else position = Math.Clamp(position, 0, duration);

        // Album art: read again when the track changes. Some players add the picture a moment after the title,
        // so keep retrying for a few seconds while it is missing.
        var key = $"{s.SourceAppUserModelId}|{p.Title}|{p.Artist}|{p.AlbumTitle}";
        if (key != artKey)
        {
            artKey = key;
            artTries = 0;
            ArtBase64 = null;
            ArtVersion++;
        }
        if (ArtBase64 == null && artTries < 5)
        {
            artTries++;
            var art = await ReadThumbnail(p.Thumbnail);
            if (art != null) { ArtBase64 = art; ArtVersion++; }
        }

        return new MediaInfo(p.Title ?? "", p.Artist ?? "", AppName(s.SourceAppUserModelId), playing,
            Math.Round(position), Math.Round(duration));
    }

    public async Task Command(string action)
    {
        var s = await Session();
        if (s == null) return;
        _ = action switch
        {
            "playpause" => await s.TryTogglePlayPauseAsync(),
            "next" => await s.TrySkipNextAsync(),
            "prev" => await s.TrySkipPreviousAsync(),
            _ => false,
        };
    }

    static async Task<string?> ReadThumbnail(IRandomAccessStreamReference? reference)
    {
        if (reference == null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 4_000_000) return null;
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    static string AppName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var name = id;
        var bang = name.LastIndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        // installed web apps report a random hex id instead of a name
        if (name.Length >= 8 && name.All(Uri.IsHexDigit)) return "";
        return name.ToLowerInvariant() switch
        {
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            "spotify" => "Spotify",
            "zunemusic" => "Media Player",
            "vlc" => "VLC",
            _ => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name,
        };
    }
}
