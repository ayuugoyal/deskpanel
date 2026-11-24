namespace DeskPanel.Agent;

static class Program
{
    public const int HttpPort = 47811;
    public const int DiscoveryPort = 47810;

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\DeskPanelAgent", out bool first);
        if (!first) return;

        Log.Info("starting");
        var cts = new CancellationTokenSource();
        var hub = new Hub();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{HttpPort}");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024);

        var app = builder.Build();
        // Ping every 5 s and drop a phone that stops answering within 10 s (Wi-Fi gone, phone asleep), so it never
        // lingers as a ghost that makes Claude wait for an answer that cannot come.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5), KeepAliveTimeout = TimeSpan.FromSeconds(10) });
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path != "/ws") { await next(ctx); return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.Serve(ws, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
        });

        // Claude Code hooks (configured in ~/.claude/settings.json) post here. Only from this computer, JSON only.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path != "/claude/hook") { await next(ctx); return; }
            var ip = ctx.Connection.RemoteIpAddress;
            // The Host check also blocks DNS-rebinding pages, which reach 127.0.0.1 under their own host name.
            var host = ctx.Request.Host.Host;
            bool localHost = host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            if (ctx.Request.Method != "POST" || ip == null || !System.Net.IPAddress.IsLoopback(ip) || !localHost
                || ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            string body;
            using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
            System.Text.Json.Nodes.JsonObject? hook = null;
            try { hook = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject; } catch { }
            var answer = hook == null ? "" : await hub.Claude.HandleHook(hook, () => hub.ClientCount > 0, ctx.RequestAborted);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(answer);
        });
        app.Run(ctx => ctx.Response.WriteAsync("DeskPanel agent"));

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Error("could not start the server", e);
            MessageBox.Show($"DeskPanel could not start: {e.Message}", "DeskPanel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _ = Task.Run(() => Discovery.Run(cts.Token));
        _ = Task.Run(() => hub.Loop(cts.Token));
        Log.Info($"ready on port {HttpPort}, discovery on UDP {DiscoveryPort}");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(hub, cts.Cancel));

        app.StopAsync().Wait(TimeSpan.FromSeconds(3));
        Log.Info("stopped");
    }
}
