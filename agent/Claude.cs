using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskPanel.Agent;

sealed class ClaudeSession
{
    public required string Id;
    public string Project = "";
    public string State = "idle"; // working | waiting | input | done | idle
    public string Activity = "";
    public string Prompt = "";
    public DateTime Since = DateTime.UtcNow;
    public DateTime Started = DateTime.UtcNow;
    public DateTime LastEvent = DateTime.UtcNow;
    public PendingPermission? Pending;
    public string Title = "";
    public bool FromRegistry;

    // battle stats
    public int Tools, Edits, Commands, Reads, Searches, Prompts, Failures;
}

sealed class PendingPermission
{
    public required string Id;
    public required string SessionId;
    public required string Tool;
    public required string Detail;
    public required DateTime Expires;
    public readonly TaskCompletionSource<string> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Tracks Claude Code sessions from their hooks (POST /claude/hook) and lets the phone answer permission requests.
/// </summary>
sealed class ClaudeTracker
{
    public const int ApprovalSeconds = 60;
    /// <summary>Longest command/detail the phone may approve. Longer ones are left to the terminal so nothing hidden is approved.</summary>
    public const int MaxApprovalDetail = 600;

    const string AllowJson = """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""";
    const string DenyJson = """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"Denied from the DeskPanel phone."}}}""";

    readonly object gate = new();
    readonly Dictionary<string, ClaudeSession> sessions = new();

    public event Action? Changed;

    /// <summary>Time-based housekeeping. Call regularly (the hub loop does); <see cref="Snapshot"/> only reads.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        lock (gate)
        {
            foreach (var s in sessions.Values.ToList())
            {
                var quiet = now - s.LastEvent;
                // Sessions still listed in ~/.claude/sessions are removed by Scan() when their process ends; dropping
                // them here would only make Scan() re-create them with their counters reset.
                if (quiet > TimeSpan.FromHours(3) && !s.FromRegistry) { sessions.Remove(s.Id); continue; }
                if (s.State == "done" && quiet > TimeSpan.FromMinutes(10)) Set(s, "idle", "Sleeping");
                if (s.State == "working" && quiet > TimeSpan.FromMinutes(15)) Set(s, "idle", "Quiet for a while");
            }
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            return sessions.Values
                .OrderBy(s => s.Started)
                .Select(s => new
                {
                    id = s.Id,
                    project = s.Project,
                    title = s.Title,
                    state = s.State,
                    activity = s.Activity,
                    prompt = s.Prompt,
                    since = Ms(s.Since),
                    started = Ms(s.Started),
                    stats = new { tools = s.Tools, edits = s.Edits, commands = s.Commands, reads = s.Reads, searches = s.Searches, prompts = s.Prompts, failures = s.Failures },
                    pending = s.Pending is { } p
                        ? new { id = p.Id, tool = p.Tool, detail = p.Detail, expires = Ms(p.Expires) }
                        : null,
                })
                .ToList();
        }
    }

    static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    public async Task<string> HandleHook(JsonObject h, Func<bool> phoneConnected, CancellationToken ct)
    {
        var ev = Str(h, "hook_event_name");
        var sid = Str(h, "session_id");
        if (sid.Length == 0) return "";

        PendingPermission? pending = null;
        lock (gate)
        {
            if (ev == "SessionEnd")
            {
                if (sessions.Remove(sid, out var ended)) ended.Pending?.Decision.TrySetResult("");
            }
            else
            {
                if (!sessions.TryGetValue(sid, out var s)) sessions[sid] = s = new ClaudeSession { Id = sid };
                var cwd = Str(h, "cwd");
                // the session's own folder (from ~/.claude/sessions) wins over the shell's current folder
                if (cwd.Length > 0 && !s.FromRegistry) s.Project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                s.LastEvent = DateTime.UtcNow;

                var tool = Str(h, "tool_name");
                var input = h["tool_input"] as JsonObject;
                switch (ev)
                {
                    case "SessionStart":
                        Set(s, "idle", "Ready");
                        break;
                    case "UserPromptSubmit":
                        s.Prompts++;
                        s.Prompt = Trunc(OneLine(Str(h, "prompt")), 140);
                        Set(s, "working", "Thinking…");
                        break;
                    case "PreToolUse":
                        Count(s, tool);
                        Set(s, "working", Describe(tool, input));
                        break;
                    case "PostToolUse":
                        if (s.State != "working") Set(s, "working", "Thinking…");
                        break;
                    case "PostToolUseFailure":
                        s.Failures++;
                        if (s.State != "working") Set(s, "working", "Thinking…");
                        break;
                    case "PermissionRequest" when tool is "AskUserQuestion" or "ExitPlanMode":
                        // questions and plans are answered in the terminal, not with Allow/Deny
                        Set(s, "input", "Waiting for you");
                        break;
                    case "PermissionRequest":
                        var detail = Detail(tool, input);
                        if (detail.Length > MaxApprovalDetail)
                        {
                            // the phone could only show part of it: never approve what cannot be read in full
                            Set(s, "waiting", "Asking in the terminal");
                            break;
                        }
                        Set(s, "waiting", $"Wants to use {FriendlyTool(tool)}");
                        if (phoneConnected())
                        {
                            pending = new PendingPermission
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                SessionId = sid,
                                Tool = FriendlyTool(tool),
                                Detail = detail,
                                Expires = DateTime.UtcNow.AddSeconds(ApprovalSeconds),
                            };
                            s.Pending?.Decision.TrySetResult("");
                            s.Pending = pending;
                        }
                        break;
                    case "Notification":
                        var type = Str(h, "notification_type");
                        if (type == "idle_prompt") Set(s, "input", "Waiting for you");
                        else if (type == "permission_prompt" && s.Pending == null) Set(s, "waiting", "Asking in the terminal");
                        break;
                    case "Stop":
                        s.Pending?.Decision.TrySetResult("");
                        s.Pending = null;
                        Set(s, "done", "Finished");
                        break;
                }
            }
        }
        Changed?.Invoke();

        if (pending == null) return "";

        string decision = "";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(ApprovalSeconds));
            decision = await pending.Decision.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate)
            {
                if (sessions.TryGetValue(pending.SessionId, out var s) && s.Pending == pending)
                {
                    s.Pending = null;
                    if (decision == "") Set(s, "waiting", "Asking in the terminal");
                }
            }
            Changed?.Invoke();
        }
        Log.Info($"permission {pending.Tool}: {(decision == "" ? "left to the terminal" : decision)}");
        return decision switch { "allow" => AllowJson, "deny" => DenyJson, _ => "" };
    }

    public void Decide(string pendingId, string decision)
    {
        if (decision is not ("allow" or "deny")) return;
        PendingPermission? p = null;
        lock (gate)
        {
            var s = sessions.Values.FirstOrDefault(x => x.Pending?.Id == pendingId);
            if (s?.Pending is { } found)
            {
                p = found;
                s.Pending = null;
                Set(s, "working", decision == "allow" ? $"Allowed {found.Tool}" : $"Denied {found.Tool}");
            }
        }
        p?.Decision.TrySetResult(decision);
        Changed?.Invoke();
    }

    /// <summary>The last phone went away: let every waiting permission request fall back to the terminal now.</summary>
    public void ReleasePending()
    {
        List<PendingPermission> waiting;
        lock (gate) waiting = sessions.Values.Where(s => s.Pending != null).Select(s => s.Pending!).ToList();
        foreach (var p in waiting) p.Decision.TrySetResult("");
    }

    // ---------------------------------------------------------------- open sessions (~/.claude/sessions/<pid>.json)

    static readonly string RegistryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    /// <summary>
    /// Every running Claude Code session keeps a small file in ~/.claude/sessions. Reading those lists all open
    /// sessions, idle ones included, and drops sessions whose process has gone away.
    /// </summary>
    public void Scan()
    {
        var alive = new Dictionary<string, (string Cwd, string Name, string Status, long Started)>();
        try
        {
            if (Directory.Exists(RegistryDir))
            {
                foreach (var file in Directory.EnumerateFiles(RegistryDir, "*.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        var r = doc.RootElement;
                        if (!r.TryGetProperty("pid", out var pidEl) || !r.TryGetProperty("sessionId", out var sidEl)) continue;
                        if (!IsClaudeProcess(pidEl.GetInt32())) continue;
                        var sid = sidEl.GetString() ?? "";
                        if (sid.Length == 0) continue;
                        alive[sid] = (
                            r.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "",
                            r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            r.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                            r.TryGetProperty("startedAt", out var sa) && sa.ValueKind == JsonValueKind.Number ? sa.GetInt64() : 0);
                    }
                    catch { /* file being written or not ours */ }
                }
            }
        }
        catch (Exception e) { Log.Error("session scan", e); return; }

        bool changed = false;
        var now = DateTime.UtcNow;
        lock (gate)
        {
            foreach (var (sid, info) in alive)
            {
                var project = Path.GetFileName(info.Cwd.TrimEnd('\\', '/'));
                if (!sessions.TryGetValue(sid, out var s))
                {
                    s = new ClaudeSession
                    {
                        Id = sid,
                        Started = info.Started > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(info.Started).UtcDateTime : now,
                        LastEvent = now.AddMinutes(-1),
                    };
                    Set(s, info.Status == "busy" ? "working" : "idle", info.Status == "busy" ? "Working…" : "Open");
                    sessions[sid] = s;
                    changed = true;
                }
                if (s.Project != project || s.Title != info.Name || !s.FromRegistry) changed = true;
                s.Project = project;
                s.Title = info.Name;
                s.FromRegistry = true;

                // Without a recent hook event, fall back to the coarse busy/idle status.
                if (now - s.LastEvent > TimeSpan.FromSeconds(20) && s.Pending == null)
                {
                    if (info.Status == "busy" && s.State is "idle" or "done") { Set(s, "working", "Working…"); changed = true; }
                    else if (info.Status == "idle" && s.State == "working") { Set(s, "done", "Finished"); changed = true; }
                }
            }
            foreach (var s in sessions.Values.Where(s => !alive.ContainsKey(s.Id)).ToList())
            {
                // gone from the registry: closed (keep hook-only sessions for a short while)
                if (s.FromRegistry || now - s.LastEvent > TimeSpan.FromSeconds(60))
                {
                    s.Pending?.Decision.TrySetResult("");
                    sessions.Remove(s.Id);
                    changed = true;
                }
            }
        }
        if (changed) Changed?.Invoke();
    }

    static bool IsClaudeProcess(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- helpers

    static void Count(ClaudeSession s, string tool)
    {
        s.Tools++;
        switch (tool)
        {
            case "Edit" or "MultiEdit" or "Write" or "NotebookEdit": s.Edits++; break;
            case "Bash" or "PowerShell": s.Commands++; break;
            case "Read": s.Reads++; break;
            case "Grep" or "Glob" or "WebSearch" or "WebFetch": s.Searches++; break;
        }
    }

    static void Set(ClaudeSession s, string state, string activity)
    {
        if (s.State != state) s.Since = DateTime.UtcNow;
        s.State = state;
        s.Activity = activity;
    }

    static string Describe(string tool, JsonObject? input) => tool switch
    {
        "Bash" or "PowerShell" => "Running " + Trunc(OneLine(Str(input, "command")), 80),
        "Edit" or "MultiEdit" => "Editing " + FileName(Str(input, "file_path")),
        "Write" => "Writing " + FileName(Str(input, "file_path")),
        "Read" => "Reading " + FileName(Str(input, "file_path")),
        "NotebookEdit" => "Editing " + FileName(Str(input, "notebook_path")),
        "Grep" => "Searching for " + Trunc(Str(input, "pattern"), 60),
        "Glob" => "Looking for " + Trunc(Str(input, "pattern"), 60),
        "WebFetch" => "Reading " + Host(Str(input, "url")),
        "WebSearch" => "Searching the web: " + Trunc(Str(input, "query"), 60),
        "Task" or "Agent" => "Agent: " + Trunc(Str(input, "description"), 60),
        "TodoWrite" => "Planning",
        _ => FriendlyTool(tool),
    };

    static string Detail(string tool, JsonObject? input)
    {
        string d = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Edit" or "MultiEdit" or "Write" or "Read" => Str(input, "file_path"),
            "NotebookEdit" => Str(input, "notebook_path"),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            _ => input?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "",
        };
        return d;
    }

    static string FriendlyTool(string tool)
    {
        if (tool.StartsWith("mcp__"))
        {
            var parts = tool.Split("__");
            return parts.Length >= 3 ? $"{parts[^1]} ({parts[1]})" : tool;
        }
        return tool.Length == 0 ? "a tool" : tool;
    }

    static string Str(JsonObject? o, string key)
    {
        try { return o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : ""; }
        catch { return ""; }
    }

    static string FileName(string path) => path.Length == 0 ? "a file" : Path.GetFileName(path);

    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : Trunc(url, 60);

    static string OneLine(string s) => s.ReplaceLineEndings(" ").Trim();

    static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
