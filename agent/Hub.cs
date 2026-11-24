using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskPanel.Agent;

sealed class Client(WebSocket ws)
{
    public WebSocket Ws { get; } = ws;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public int ArtSent = -1;
    public int LyricsSent = -1;
}

/// <summary>Keeps the connected (paired) phones and pushes the laptop's state to them once a second.</summary>
sealed class Hub
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const int MaxMessage = 64 * 1024;

    readonly ConcurrentDictionary<Client, byte> clients = new();
    readonly SystemStats stats = new();
    readonly MediaSource media = new();
    readonly Lyrics lyrics = new();
    readonly SemaphoreSlim buildLock = new(1, 1);
    StatsSample? lastStats;
    int pushQueued;

    // Unknown phones trigger a dialog on the laptop. After a refusal or timeout the same address must wait,
    // and only a few unauthenticated sockets may be open at once, so nothing on the LAN can flood the laptop with pop-ups.
    static readonly TimeSpan PairCooldown = TimeSpan.FromSeconds(20);
    const int MaxUnauthenticated = 8;
    readonly ConcurrentDictionary<string, DateTime> pairBlockedUntil = new();
    int unauthenticated;

    public ClaudeTracker Claude { get; } = new();
    public int ClientCount => clients.Count;

    /// <summary>Drops every connected phone (e.g. after "Forget paired phones"); they must pair again to come back.</summary>
    public void DisconnectAll()
    {
        foreach (var c in clients.Keys) c.Ws.Abort();
    }

    sealed record Snapshot(string StateJson, int ArtVersion, string? Art, int LyricsVersion, string LyricsJson);

    public Hub()
    {
        Claude.Changed += PushSoon;
        lyrics.Changed += PushSoon;
    }

    public async Task Serve(WebSocket ws, IPAddress? remote, CancellationToken ct)
    {
        var c = new Client(ws);
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        var address = remote?.MapToIPv4().ToString() ?? "?";
        bool counted = true;
        if (Interlocked.Increment(ref unauthenticated) > MaxUnauthenticated)
        {
            Interlocked.Decrement(ref unauthenticated);
            ws.Abort();
            return;
        }
        try
        {
            // 1. The phone introduces itself with its key; unknown phones must be approved on the laptop once.
            string? helloText;
            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                helloText = await ReceiveText(ws, buf, ms, helloTimeout.Token);
            }
            if (helloText == null || JsonNode.Parse(helloText) is not JsonObject hello || (string?)hello["cmd"] != "hello") return;

            var token = (string?)hello["token"];
            Task<string?>? carried = null;
            if (!Pairing.IsKnown(token))
            {
                if (pairBlockedUntil.TryGetValue(address, out var until) && until > DateTime.UtcNow)
                {
                    await Send(c, JsonSerializer.Serialize(new { type = "rejected" }, Json));
                    return;
                }
                var name = Trunc((string?)hello["name"] ?? "Phone", 40);
                await Send(c, JsonSerializer.Serialize(new { type = "pairing" }, Json));

                // Keep a receive pending while the laptop dialog is open: it answers the phone's pings (so the
                // socket survives a slow click) and tells us if the phone leaves (then the dialog is skipped).
                carried = ReceiveText(ws, buf, ms, ct);
                using var askCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ask = Pairing.Ask(name, address, askCts.Token);
                if (await Task.WhenAny(ask, carried) == carried)
                {
                    askCts.Cancel(); // the phone left or spoke out of turn
                    await ask;
                    return;
                }
                var newToken = await ask;
                if (newToken == null)
                {
                    pairBlockedUntil[address] = DateTime.UtcNow + PairCooldown;
                    await Send(c, JsonSerializer.Serialize(new { type = "rejected" }, Json));
                    return;
                }
                await Send(c, JsonSerializer.Serialize(new { type = "paired", token = newToken }, Json));
            }

            // 2. Paired: stream the state, accept commands.
            clients[c] = 0;
            counted = false;
            Interlocked.Decrement(ref unauthenticated);
            Log.Info($"phone connected from {remote} ({clients.Count} total)");
            await PushTo(c, await Build(sampleStats: false), skipIfBusy: false);
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var text = carried != null ? await carried : await ReceiveText(ws, buf, ms, ct);
                carried = null;
                if (text == null) break;
                await Handle(text);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException) { }
        catch (Exception e) { Log.Error("client", e); }
        finally
        {
            if (counted) Interlocked.Decrement(ref unauthenticated);
            if (clients.TryRemove(c, out _))
            {
                Log.Info($"phone disconnected ({clients.Count} left)");
                // nobody left to answer: hand any waiting permission requests back to the terminal right away
                if (clients.IsEmpty) Claude.ReleasePending();
            }
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            }
        }
    }

    static async Task<string?> ReceiveText(WebSocket ws, byte[] buf, MemoryStream ms, CancellationToken ct)
    {
        ms.SetLength(0);
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > MaxMessage) return null;
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    public async Task Loop(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Claude.Tick();
                if (tick++ % 2 == 0) Claude.Scan();
                if (!clients.IsEmpty) await Broadcast(await Build(sampleStats: true));
            }
            catch (Exception e) { Log.Error("loop", e); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Push right away (coalesced), e.g. when a Claude session changes.</summary>
    void PushSoon()
    {
        if (clients.IsEmpty || Interlocked.Exchange(ref pushQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            Interlocked.Exchange(ref pushQueued, 0);
            try { await Broadcast(await Build(sampleStats: false)); } catch (Exception e) { Log.Error("push", e); }
        });
    }

    async Task Handle(string text)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(text); } catch { return; }
        if (n is not JsonObject o) return;

        switch ((string?)o["cmd"])
        {
            case "media":
                await media.Command((string?)o["action"] ?? "");
                await Task.Delay(250); // give the player a moment to report its new state
                break;
            case "volume":
                if (o["level"] is JsonValue lv && lv.TryGetValue<double>(out var level))
                    Volume.SetLevel((int)Math.Round(Math.Clamp(level, 0, 100)));
                break;
            case "mute":
                if (o["muted"] is JsonValue mv && mv.TryGetValue<bool>(out var muted))
                    Volume.SetMute(muted);
                break;
            case "claude":
                Claude.Decide((string?)o["id"] ?? "", (string?)o["decision"] ?? "");
                return; // Decide triggers its own push
            default:
                return;
        }
        await Broadcast(await Build(sampleStats: false));
    }

    async Task<Snapshot> Build(bool sampleStats)
    {
        await buildLock.WaitAsync();
        try
        {
            if (sampleStats || lastStats == null) lastStats = stats.Sample();
            var s = lastStats;

            MediaInfo? m = null;
            try { m = await media.Get(); } catch (Exception e) { Log.Error("media", e); }
            if (m != null && m.Title.Length > 0) lyrics.Track(m.Title, m.Artist, m.Duration);
            else lyrics.Clear();
            var vol = Volume.Get();
            var lyr = lyrics.Current();

            var state = new
            {
                type = "state",
                host = Environment.MachineName,
                cpu = Math.Round(s.Cpu, 1),
                ram = new { used = s.RamUsed, total = s.RamTotal },
                disk = new { used = s.DiskUsed, total = s.DiskTotal },
                battery = s.BatteryPercent is int p ? new { percent = p, charging = s.Charging } : null,
                net = new { down = s.NetDown, up = s.NetUp },
                uptime = s.UptimeSec,
                volume = vol is { } v ? new { level = v.Level, muted = v.Muted } : null,
                media = m is null ? null : new { m.Title, m.Artist, m.App, m.Playing, m.Position, m.Duration },
                artVersion = media.ArtVersion,
                claude = Claude.Snapshot(),
                lyricsVersion = lyr.Version,
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            var lyricsJson = JsonSerializer.Serialize(new { type = "lyrics", version = lyr.Version, lines = lyr.Lines }, Json);
            return new Snapshot(JsonSerializer.Serialize(state, Json), media.ArtVersion, media.ArtBase64, lyr.Version, lyricsJson);
        }
        finally { buildLock.Release(); }
    }

    // A phone still busy with the previous push (slow Wi-Fi) skips this one instead of holding up the others.
    Task Broadcast(Snapshot s) => Task.WhenAll(clients.Keys.Select(c => PushTo(c, s, skipIfBusy: true)));

    static async Task PushTo(Client c, Snapshot s, bool skipIfBusy)
    {
        if (skipIfBusy) { if (!await c.SendLock.WaitAsync(0)) return; }
        else await c.SendLock.WaitAsync();
        try
        {
            if (c.Ws.State != WebSocketState.Open) return;
            if (c.ArtSent != s.ArtVersion)
            {
                await SendText(c.Ws, JsonSerializer.Serialize(new { type = "art", version = s.ArtVersion, data = s.Art }, Json));
                c.ArtSent = s.ArtVersion;
            }
            if (c.LyricsSent != s.LyricsVersion)
            {
                await SendText(c.Ws, s.LyricsJson);
                c.LyricsSent = s.LyricsVersion;
            }
            await SendText(c.Ws, s.StateJson);
        }
        catch
        {
            c.Ws.Abort();
        }
        finally { c.SendLock.Release(); }
    }

    static async Task Send(Client c, string text)
    {
        await c.SendLock.WaitAsync();
        try { await SendText(c.Ws, text); }
        finally { c.SendLock.Release(); }
    }

    static async Task SendText(WebSocket ws, string text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, timeout.Token);
    }

    static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}
